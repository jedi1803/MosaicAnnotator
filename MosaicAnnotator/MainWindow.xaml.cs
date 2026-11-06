using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Storage;
using Windows.Storage.Pickers;
using System.Text.Json;

namespace MosaicAnnotator
{
    public class MosaicLineData
    {
        public bool IsVertical { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Length { get; set; }
    }

    public class CropWindowData
    {
        public double X { get; set; }
        public double Y { get; set; }
        public int Size { get; set; }
    }

    public class MosaicProjectData
    {
        public string ImageFileName { get; set; } = string.Empty;
        public CropWindowData CropWindow { get; set; } = new CropWindowData();
        public List<MosaicLineData> MosaicLines { get; set; } = new List<MosaicLineData>();
    }

    public sealed partial class MainWindow : Window
    {
        // Holds the hardware-accelerated image
        private CanvasBitmap? _currentImage;
        private List<StorageFile> _imageFiles = new List<StorageFile>();
        private int _currentIndex = -1;

        // Region Mode State
        private bool _isRegionMode = false;
        private bool _isMosaicMode = false;
        private bool _isDraggingRegion = false;
        private Windows.Foundation.Point _lastMousePos;
        private double _regionX = 0;
        private double _regionY = 0;
        private readonly int[] _cropSizes = { 256, 512, 768, 1024 };

        private List<MosaicEdge> _mosaicEdges = new List<MosaicEdge>();
        private MosaicEdge? _activeEdge = null;
        private bool _nextEdgeIsVertical = false;

        private enum DragMode { None, Body, StartHandle, EndHandle }
        private DragMode _dragMode = DragMode.None;

        private bool _isSelectingLineEnds = false;
        private Windows.Foundation.Point _selectionStartPos;

        public MainWindow()
        {
            this.InitializeComponent();
        }

        private async void LoadImageButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker();
            picker.ViewMode = PickerViewMode.Thumbnail;
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                _imageFiles.Clear();
                _imageFiles.Add(file);
                _currentIndex = 0;
                await DisplaySelectedImageAsync();
            }
        }

        private async void LoadDirectoryButton_Click(object sender, RoutedEventArgs e)
        {
            var picker = new FolderPicker();
            picker.ViewMode = PickerViewMode.List;
            picker.FileTypeFilter.Add("*");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
            {
                var files = await folder.GetFilesAsync();
                _imageFiles.Clear();

                foreach (var file in files)
                {
                    string ext = file.FileType.ToLower();
                    // Filter out system files or non-images
                    if (ext == ".png" || ext == ".jpg")
                    {
                        _imageFiles.Add(file);
                    }
                }

                if (_imageFiles.Count > 0)
                {
                    _currentIndex = 0;
                    await DisplaySelectedImageAsync();
                }
                else
                {
                    FileStatusText.Text = "No images found in folder.";
                }
            }
        }

        private async void PrevButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentIndex > 0)
            {
                _currentIndex--;
                await DisplaySelectedImageAsync();
            }
        }

        private async void NextButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentIndex < _imageFiles.Count - 1)
            {
                _currentIndex++;
                await DisplaySelectedImageAsync();
            }
        }

        private async Task DisplaySelectedImageAsync()
        {
            if (_imageFiles.Count == 0 || _currentIndex < 0 || _currentIndex >= _imageFiles.Count)
                return;

            StorageFile file = _imageFiles[_currentIndex];

            using (var stream = await file.OpenReadAsync())
            {
                _currentImage = await CanvasBitmap.LoadAsync(ImageCanvas, stream);
            }

            // Adjust the Canvas size to match the image precisely
            ImageCanvas.Width = _currentImage.SizeInPixels.Width;
            ImageCanvas.Height = _currentImage.SizeInPixels.Height;
            OverlayCanvas.Width = _currentImage.SizeInPixels.Width;
            OverlayCanvas.Height = _currentImage.SizeInPixels.Height;

            // Force the UI to update its dimensions
            WorkspaceGrid.UpdateLayout();

            // Calculate the zoom factor needed to fit the whole image on screen
            double scaleX = ImageScrollViewer.ActualWidth / _currentImage.SizeInPixels.Width;
            double scaleY = ImageScrollViewer.ActualHeight / _currentImage.SizeInPixels.Height;
            float zoomFactor = (float)Math.Min(scaleX, scaleY);

            // Zoom out to fit the screen, leaving a 5% margin
            ImageScrollViewer.ChangeView(null, null, zoomFactor * 0.95f);

            // Update Navigation UI
            FileStatusText.Text = $"{_currentIndex + 1} / {_imageFiles.Count}";
            FileNameText.Text = file.Name;

            // We explicitly re-evaluate these in case they were disabled by Mosaic Mode
            PrevButton.IsEnabled = _currentIndex > 0;
            NextButton.IsEnabled = _currentIndex < _imageFiles.Count - 1;
            LoadDirectoryButton.IsEnabled = true;

            // Trigger a redraw
            ImageCanvas.Invalidate();

            // Start Region Mode
            _isRegionMode = true;
            UpdateRegionRectSize();
            RegionRect.Visibility = Visibility.Visible;
            RegionRect.Stroke = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Lime);

            // Attempt to restore progress if a JSON file already exists for this image
            LoadExistingJsonData();
        }

        private void LoadExistingJsonData()
        {
            if (_currentIndex < 0 || _imageFiles.Count == 0) return;

            string currentImagePath = _imageFiles[_currentIndex].Path;
            string imageDir = System.IO.Path.GetDirectoryName(currentImagePath)!;
            string jsonDir = System.IO.Path.Combine(imageDir, "json");
            string jsonFileName = System.IO.Path.GetFileNameWithoutExtension(currentImagePath) + "_mosaic.json";
            string fullJsonPath = System.IO.Path.Combine(jsonDir, jsonFileName);

            // 1. Reset current canvas state before loading
            foreach (var edge in _mosaicEdges)
            {
                edge.RemoveFromCanvas(OverlayCanvas);
            }
            _mosaicEdges.Clear();

            // Do NOT use OverlayCanvas.Children.Clear() here!

            _isMosaicMode = false;
            _isRegionMode = true;
            RegionRect.Visibility = Visibility.Collapsed;

            // 2. Check if previous annotation exists
            if (System.IO.File.Exists(fullJsonPath))
            {
                try
                {
                    string jsonString = System.IO.File.ReadAllText(fullJsonPath);
                    var projectData = JsonSerializer.Deserialize<MosaicProjectData>(jsonString);

                    if (projectData != null)
                    {
                        // Restore Crop Window
                        _regionX = projectData.CropWindow.X;
                        _regionY = projectData.CropWindow.Y;

                        // Match ComboBox to saved size
                        for (int i = 0; i < _cropSizes.Length; i++)
                        {
                            if (_cropSizes[i] == projectData.CropWindow.Size)
                            {
                                CropSizeComboBox.SelectedIndex = i;
                                break;
                            }
                        }

                        // Restore Lines
                        foreach (var lineData in projectData.MosaicLines)
                        {
                            var edge = new MosaicEdge(lineData.IsVertical, lineData.X, lineData.Y, lineData.Length);
                            edge.AddToCanvas(OverlayCanvas);
                            _mosaicEdges.Add(edge);
                        }

                        // Always stay in Region Mode initially so the user sees the full image
                        _isRegionMode = true;
                        _isMosaicMode = false;

                        RegionRect.Width = projectData.CropWindow.Size;
                        RegionRect.Height = projectData.CropWindow.Size;
                        Canvas.SetLeft(RegionRect, _regionX);
                        Canvas.SetTop(RegionRect, _regionY);
                        RegionRect.Visibility = Visibility.Visible;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to load existing JSON: {ex.Message}");
                }
            }
            else
            {
                // Ensure RegionRect is shown if no JSON existed
                RegionRect.Visibility = Visibility.Visible;
            }
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentImage == null || _currentIndex < 0 || _imageFiles.Count == 0) return;

            // 1. Calculate the JSON path
            string currentImagePath = _imageFiles[_currentIndex].Path;
            string imageDir = System.IO.Path.GetDirectoryName(currentImagePath)!;
            string jsonDir = System.IO.Path.Combine(imageDir, "json");
            string jsonFileName = System.IO.Path.GetFileNameWithoutExtension(currentImagePath) + "_mosaic.json";
            string fullJsonPath = System.IO.Path.Combine(jsonDir, jsonFileName);

            // 2. If it exists, force delete it
            if (System.IO.File.Exists(fullJsonPath))
            {
                try
                {
                    // Force remove any Read-Only attributes that might block deletion
                    System.IO.File.SetAttributes(fullJsonPath, System.IO.FileAttributes.Normal);

                    System.IO.File.Delete(fullJsonPath);
                }
                catch (Exception ex)
                {
                    // If Windows blocks the deletion, display the exact error in the top right corner!
                    FileNameText.Text = "DELETE ERROR: " + ex.Message;
                }
            }

            // 3. Clear any mosaic lines from the UI and memory
            foreach (var edge in _mosaicEdges)
            {
                edge.RemoveFromCanvas(OverlayCanvas);
            }
            _mosaicEdges.Clear();

            // 4. Ensure we are in Region Mode with the current square size and location
            _isMosaicMode = false;
            _isRegionMode = true;
            UpdateRegionRectSize();
            RegionRect.Visibility = Visibility.Visible;
        }

        private void UpdateRegionRectSize()
        {
            if (_currentImage == null) return;

            int size = _cropSizes[CropSizeComboBox.SelectedIndex];
            double imgWidth = _currentImage.SizeInPixels.Width;
            double imgHeight = _currentImage.SizeInPixels.Height;

            // Clamp to ensure the new size doesn't push the square outside the image
            if (_regionX + size > imgWidth) _regionX = Math.Max(0, imgWidth - size);
            if (_regionY + size > imgHeight) _regionY = Math.Max(0, imgHeight - size);

            RegionRect.Width = size;
            RegionRect.Height = size;
            Canvas.SetLeft(RegionRect, _regionX);
            Canvas.SetTop(RegionRect, _regionY);
        }

        private void CropSizeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isRegionMode) UpdateRegionRectSize();
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentImage == null || _currentIndex < 0 || _imageFiles.Count == 0) return;

            // 1. Package the data
            var projectData = new MosaicProjectData
            {
                ImageFileName = _imageFiles[_currentIndex].Name,
                CropWindow = new CropWindowData
                {
                    X = _regionX,
                    Y = _regionY,
                    Size = _cropSizes[CropSizeComboBox.SelectedIndex]
                }
            };

            foreach (var edge in _mosaicEdges)
            {
                projectData.MosaicLines.Add(new MosaicLineData
                {
                    IsVertical = edge.IsVertical,
                    X = edge.X,
                    Y = edge.Y,
                    Length = edge.Length
                });
            }

            // 2. Serialize to formatted JSON string
            var options = new JsonSerializerOptions { WriteIndented = true };
            string jsonString = JsonSerializer.Serialize(projectData, options);

            // 3. Define directory paths based on the current image's location
            string currentImagePath = _imageFiles[_currentIndex].Path;

            // The '!' at the very end suppresses the null warning
            string imageDir = System.IO.Path.GetDirectoryName(currentImagePath)!;

            string jsonDir = System.IO.Path.Combine(imageDir, "json");

            // Ensure the 'json' directory exists
            System.IO.Directory.CreateDirectory(jsonDir);

            // 4. Save silently to the json folder
            string jsonFileName = System.IO.Path.GetFileNameWithoutExtension(projectData.ImageFileName) + "_mosaic.json";
            string fullSavePath = System.IO.Path.Combine(jsonDir, jsonFileName);

            System.IO.File.WriteAllText(fullSavePath, jsonString);
        }

        private void ContrastSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            // Re-draw the canvas when slider changes
            ImageCanvas?.Invalidate();
        }

        private void ImageCanvas_CreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
        {
        }

        private void ImageCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
        {
            if (_currentImage != null)
            {
                // Apply the real-time contrast effect based on your slider
                var contrastEffect = new ContrastEffect
                {
                    Source = _currentImage,
                    Contrast = (float)ContrastSlider.Value
                };

                args.DrawingSession.DrawImage(contrastEffect);
            }
        }

        private void OverlayCanvas_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(OverlayCanvas);
            var pos = point.Position;

            if (_isMosaicMode)
            {
                // Check if SHIFT key is held down
                var shiftState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
                bool isShiftPressed = (shiftState & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

                if (point.Properties.IsLeftButtonPressed && isShiftPressed)
                {
                    _isSelectingLineEnds = true;
                    _selectionStartPos = pos;
                    Canvas.SetLeft(CornerSelectionBox, pos.X);
                    Canvas.SetTop(CornerSelectionBox, pos.Y);
                    CornerSelectionBox.Width = 0;
                    CornerSelectionBox.Height = 0;
                    CornerSelectionBox.Visibility = Visibility.Visible;
                    OverlayCanvas.CapturePointer(e.Pointer);
                    e.Handled = true;
                    return;
                }

                // Right Mouse Button: Show Context Menu
                if (point.Properties.IsRightButtonPressed)
                {
                    var flyout = new Microsoft.UI.Xaml.Controls.MenuFlyout();

                    if (e.OriginalSource is FrameworkElement element && element.Tag is MosaicEdge clickedEdge)
                    {
                        // Right-clicked an existing edge: Show Toggle and Delete options
                        var toggleItem = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem { Text = "Toggle Orientation" };
                        toggleItem.Click += (s, args) => clickedEdge.ToggleOrientation();
                        flyout.Items.Add(toggleItem);

                        var deleteItem = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem { Text = "Delete Line" };
                        deleteItem.Click += (s, args) =>
                        {
                            clickedEdge.RemoveFromCanvas(OverlayCanvas);
                            _mosaicEdges.Remove(clickedEdge);
                        };
                        flyout.Items.Add(deleteItem);
                    }
                    else
                    {
                        // Right-clicked empty space: Show Creation options
                        var vItem = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem { Text = "Add Vertical Line" };
                        vItem.Click += (s, args) =>
                        {
                            var newEdge = new MosaicEdge(true, pos.X, pos.Y, 100);
                            newEdge.AddToCanvas(OverlayCanvas);
                            _mosaicEdges.Add(newEdge);
                        };
                        flyout.Items.Add(vItem);

                        var hItem = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem { Text = "Add Horizontal Line" };
                        hItem.Click += (s, args) =>
                        {
                            var newEdge = new MosaicEdge(false, pos.X, pos.Y, 100);
                            newEdge.AddToCanvas(OverlayCanvas);
                            _mosaicEdges.Add(newEdge);
                        };
                        flyout.Items.Add(hItem);
                    }

                    // Display the menu exactly at the cursor's location
                    flyout.ShowAt(OverlayCanvas, pos);
                    e.Handled = true;
                    return;
                }

                // Left Mouse Button: Drag or Resize Edge
                if (point.Properties.IsLeftButtonPressed)
                {
                    if (e.OriginalSource is FrameworkElement element && element.Tag is MosaicEdge edge)
                    {
                        _activeEdge = edge;
                        _lastMousePos = pos;

                        if (element == edge.HitboxLine) _dragMode = DragMode.Body;
                        else if (element == edge.StartHandle) _dragMode = DragMode.StartHandle;
                        else if (element == edge.EndHandle) _dragMode = DragMode.EndHandle;

                        OverlayCanvas.CapturePointer(e.Pointer);
                        e.Handled = true;
                    }
                }
                return; // Skip region mode logic if we are in mosaic mode
            }

            // --- REGION MODE LOGIC ---
            if (!_isRegionMode) return;
            int size = _cropSizes[CropSizeComboBox.SelectedIndex];
            bool isInside = pos.X >= _regionX && pos.X <= _regionX + size &&
                            pos.Y >= _regionY && pos.Y <= _regionY + size;

            if (isInside)
            {
                _isDraggingRegion = true;
                _lastMousePos = pos;
                OverlayCanvas.CapturePointer(e.Pointer);
            }
            else
            {
                _isRegionMode = false;
                _isMosaicMode = true;
                RegionRect.Visibility = Visibility.Collapsed;
                EnterMosaicMode();

                // --- Trigger auto-detection as soon as we enter Mosaic Mode ---
                
            }
        }

        private void OverlayCanvas_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            var pos = e.GetCurrentPoint(OverlayCanvas).Position;
            if (_isSelectingLineEnds)
            {
                double x = System.Math.Min(pos.X, _selectionStartPos.X);
                double y = System.Math.Min(pos.Y, _selectionStartPos.Y);
                double w = System.Math.Abs(pos.X - _selectionStartPos.X);
                double h = System.Math.Abs(pos.Y - _selectionStartPos.Y);

                Canvas.SetLeft(CornerSelectionBox, x);
                Canvas.SetTop(CornerSelectionBox, y);
                CornerSelectionBox.Width = w;
                CornerSelectionBox.Height = h;
                return;
            }

            if (_isMosaicMode && _activeEdge != null)
            {
                double dx = pos.X - _lastMousePos.X;
                double dy = pos.Y - _lastMousePos.Y;

                if (_dragMode == DragMode.Body) _activeEdge.Move(dx, dy);
                else if (_dragMode == DragMode.StartHandle) _activeEdge.ResizeStart(dx, dy);
                else if (_dragMode == DragMode.EndHandle) _activeEdge.ResizeEnd(dx, dy);

                _lastMousePos = pos;
                return;
            }

            // --- REGION MODE LOGIC ---
            if (_isDraggingRegion && _currentImage != null)
            {
                double dx = pos.X - _lastMousePos.X;
                double dy = pos.Y - _lastMousePos.Y;

                int size = _cropSizes[CropSizeComboBox.SelectedIndex];
                double imgWidth = _currentImage.SizeInPixels.Width;
                double imgHeight = _currentImage.SizeInPixels.Height;

                _regionX += dx;
                _regionY += dy;

                if (_regionX < 0) _regionX = 0;
                if (_regionY < 0) _regionY = 0;
                if (_regionX + size > imgWidth) _regionX = imgWidth - size;
                if (_regionY + size > imgHeight) _regionY = imgHeight - size;

                Canvas.SetLeft(RegionRect, _regionX);
                Canvas.SetTop(RegionRect, _regionY);

                _lastMousePos = pos;
            }
        }

        private void OverlayCanvas_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            // --- Smart Clean-Up Snapping Logic ---
            if (_isSelectingLineEnds)
            {
                _isSelectingLineEnds = false;
                CornerSelectionBox.Visibility = Visibility.Collapsed;
                OverlayCanvas.ReleasePointerCapture(e.Pointer);

                // Define the area the user selected safely
                double rectX = Canvas.GetLeft(CornerSelectionBox);
                double rectY = Canvas.GetTop(CornerSelectionBox);
                double rectW = CornerSelectionBox.Width;
                double rectH = CornerSelectionBox.Height;

                var vEdgesInBox = new List<MosaicEdge>();
                var hEdgesInBox = new List<MosaicEdge>();

                // 1. Collect all lines that touch or cross the selection box
                foreach (var edge in _mosaicEdges)
                {
                    var edgeRect = edge.IsVertical
                        ? new Windows.Foundation.Rect(edge.X - 2, edge.Y, 4, edge.Length)
                        : new Windows.Foundation.Rect(edge.X, edge.Y - 2, edge.Length, 4);

                    bool intersects = (rectX <= edgeRect.X + edgeRect.Width) &&
                                      (rectX + rectW >= edgeRect.X) &&
                                      (rectY <= edgeRect.Y + edgeRect.Height) &&
                                      (rectY + rectH >= edgeRect.Y);

                    if (intersects)
                    {
                        if (edge.IsVertical) vEdgesInBox.Add(edge);
                        else hEdgesInBox.Add(edge);
                    }
                }

                // 2. Snap any Horizontal line endpoint that falls physically inside the blue box
                foreach (var h in _mosaicEdges.Where(edge => !edge.IsVertical))
                {
                    bool startInBox = h.X >= rectX && h.X <= rectX + rectW && h.Y >= rectY && h.Y <= rectY + rectH;
                    if (startInBox && vEdgesInBox.Count > 0)
                    {
                        var nearestV = vEdgesInBox.OrderBy(v => System.Math.Abs(v.X - h.X)).First();
                        h.SnapStart(nearestV.X);
                    }

                    double endX = h.X + h.Length;
                    bool endInBox = endX >= rectX && endX <= rectX + rectW && h.Y >= rectY && h.Y <= rectY + rectH;
                    if (endInBox && vEdgesInBox.Count > 0)
                    {
                        var nearestV = vEdgesInBox.OrderBy(v => System.Math.Abs(v.X - endX)).First();
                        h.SnapEnd(nearestV.X);
                    }
                }

                // 3. Snap any Vertical line endpoint that falls physically inside the blue box
                foreach (var v in _mosaicEdges.Where(edge => edge.IsVertical))
                {
                    bool startInBox = v.X >= rectX && v.X <= rectX + rectW && v.Y >= rectY && v.Y <= rectY + rectH;
                    if (startInBox && hEdgesInBox.Count > 0)
                    {
                        var nearestH = hEdgesInBox.OrderBy(h => System.Math.Abs(h.Y - v.Y)).First();
                        v.SnapStart(nearestH.Y);
                    }

                    double endY = v.Y + v.Length;
                    bool endInBox = v.X >= rectX && v.X <= rectX + rectW && endY >= rectY && endY <= rectY + rectH;
                    if (endInBox && hEdgesInBox.Count > 0)
                    {
                        var nearestH = hEdgesInBox.OrderBy(h => System.Math.Abs(h.Y - endY)).First();
                        v.SnapEnd(nearestH.Y);
                    }
                }
                return;
            }

            // --- Mosaic Line Dragging Logic ---
            if (_isMosaicMode && _activeEdge != null)
            {
                _activeEdge = null;
                _dragMode = DragMode.None;
                OverlayCanvas.ReleasePointerCapture(e.Pointer);
                return;
            }

            // --- REGION MODE LOGIC ---
            if (_isDraggingRegion)
            {
                _isDraggingRegion = false;
                OverlayCanvas.ReleasePointerCapture(e.Pointer);
            }
        }

        private void OverlayCanvas_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (!_isRegionMode) return;

            var pt = e.GetCurrentPoint(OverlayCanvas).Position;
            int size = _cropSizes[CropSizeComboBox.SelectedIndex];

            bool isInside = pt.X >= _regionX && pt.X <= _regionX + size &&
                            pt.Y >= _regionY && pt.Y <= _regionY + size;

            if (isInside)
            {
                int delta = e.GetCurrentPoint(OverlayCanvas).Properties.MouseWheelDelta;

                // Scroll Up = Larger, Scroll Down = Smaller
                if (delta > 0 && CropSizeComboBox.SelectedIndex < _cropSizes.Length - 1)
                {
                    CropSizeComboBox.SelectedIndex++;
                }
                else if (delta < 0 && CropSizeComboBox.SelectedIndex > 0)
                {
                    CropSizeComboBox.SelectedIndex--;
                }

                // Mark handled so the ScrollViewer doesn't zoom/pan the whole screen
                e.Handled = true;
            }
        }

        private void RootGrid_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Escape && _isMosaicMode)
            {
                ExitMosaicMode();
                e.Handled = true; // Prevent the keypress from triggering other default UI behaviors
            }
        }

        private void EnterMosaicMode()
        {
            if (_currentImage == null) return;

            int size = _cropSizes[CropSizeComboBox.SelectedIndex];

            // Clip the image so ONLY the pixels inside the region are visible
            ImageCanvas.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(_regionX, _regionY, size, size)
            };

            // Calculate zoom factor to make the selected region fill the screen (with a 5% margin)
            double scaleX = ImageScrollViewer.ActualWidth / size;
            double scaleY = ImageScrollViewer.ActualHeight / size;
            float zoomFactor = (float)Math.Min(scaleX, scaleY) * 0.95f;

            // Calculate offsets to center the region exactly in the viewport
            double targetOffsetX = ((_regionX + (size / 2.0)) * zoomFactor) - (ImageScrollViewer.ActualWidth / 2.0);
            double targetOffsetY = ((_regionY + (size / 2.0)) * zoomFactor) - (ImageScrollViewer.ActualHeight / 2.0);

            targetOffsetX = Math.Max(0, targetOffsetX);
            targetOffsetY = Math.Max(0, targetOffsetY);

            ImageScrollViewer.ChangeView(targetOffsetX, targetOffsetY, zoomFactor);

            // Lock navigation while editing
            PrevButton.IsEnabled = false;
            NextButton.IsEnabled = false;
            LoadDirectoryButton.IsEnabled = false;
            ClearButton.IsEnabled = false;
        }

        private void ExitMosaicMode()
        {
            if (_currentImage == null) return;

            _isMosaicMode = false;
            _isRegionMode = true;

            // Remove the clip to reveal the whole image again
            ImageCanvas.Clip = null;

            // Bring back the green square
            RegionRect.Visibility = Visibility.Visible;

            // Zoom back out to fit the full 1920x1080 image
            double scaleX = ImageScrollViewer.ActualWidth / _currentImage.SizeInPixels.Width;
            double scaleY = ImageScrollViewer.ActualHeight / _currentImage.SizeInPixels.Height;
            float zoomFactor = (float)Math.Min(scaleX, scaleY) * 0.95f;

            ImageScrollViewer.ChangeView(null, null, zoomFactor);

            // Unlock navigation
            PrevButton.IsEnabled = _currentIndex > 0;
            NextButton.IsEnabled = _currentIndex < _imageFiles.Count - 1;
            LoadDirectoryButton.IsEnabled = true;
            ClearButton.IsEnabled = true;
        }
    }
}