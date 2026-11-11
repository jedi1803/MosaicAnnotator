using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage;

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
        private CanvasBitmap? _currentImage;
        private List<StorageFile> _imageFiles = new List<StorageFile>();
        private int _currentIndex = -1;

        private bool _isRegionMode = false;
        private bool _isMosaicMode = false;
        private bool _isDraggingRegion = false;
        private Windows.Foundation.Point _lastMousePos;
        private double _regionX = 0;
        private double _regionY = 0;
        private readonly int[] _cropSizes = { 256, 512, 768, 1024 };

        private List<MosaicEdge> _mosaicEdges = new List<MosaicEdge>();
        private MosaicEdge? _activeEdge = null;

        private enum DragMode { None, Body, StartHandle, EndHandle }
        private DragMode _dragMode = DragMode.None;

        private bool _isSelectingLineEnds = false;
        private Windows.Foundation.Point _selectionStartPos;
        private Windows.Foundation.Rect _cornerSelectionRect;

        private bool _isPanning = false;
        private Windows.Foundation.Point _lastPanScreenPos;

        private HashSet<MosaicEdge> _selectedEdges = new HashSet<MosaicEdge>();

        public MainWindow()
        {
            this.InitializeComponent();
        }

        private async void LoadImageButton_Click(object sender, RoutedEventArgs e)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            string? selectedPath = NativeInterop.PickFile(hwnd);

            if (!string.IsNullOrEmpty(selectedPath))
            {
                var file = await StorageFile.GetFileFromPathAsync(selectedPath);
                _imageFiles.Clear();
                _imageFiles.Add(file);
                _currentIndex = 0;
                await DisplaySelectedImageAsync();
            }
        }

        private async void LoadDirectoryButton_Click(object sender, RoutedEventArgs e)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            string? selectedPath = NativeInterop.PickFolder(hwnd);

            if (!string.IsNullOrEmpty(selectedPath))
            {
                var folder = await StorageFolder.GetFolderFromPathAsync(selectedPath);
                var files = await folder.GetFilesAsync();
                _imageFiles.Clear();

                foreach (var file in files)
                {
                    string ext = file.FileType.ToLower();
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

            ImageCanvas.Width = _currentImage.SizeInPixels.Width;
            ImageCanvas.Height = _currentImage.SizeInPixels.Height;
            OverlayCanvas.Width = _currentImage.SizeInPixels.Width;
            OverlayCanvas.Height = _currentImage.SizeInPixels.Height;

            ImageWorkArea.UpdateLayout();

            FileStatusText.Text = $"{_currentIndex + 1} / {_imageFiles.Count}";
            FileNameText.Text = file.Name;

            PrevButton.IsEnabled = _currentIndex > 0;
            NextButton.IsEnabled = _currentIndex < _imageFiles.Count - 1;
            LoadDirectoryButton.IsEnabled = true;

            ImageCanvas.Invalidate();

            _isRegionMode = true;
            UpdateRegionRectSize();
            LoadExistingJsonData();
            await ZoomToFitImage();
        }

        private void LoadExistingJsonData()
        {
            if (_currentIndex < 0 || _imageFiles.Count == 0) return;

            string currentImagePath = _imageFiles[_currentIndex].Path;
            string imageDir = System.IO.Path.GetDirectoryName(currentImagePath)!;
            string jsonDir = System.IO.Path.Combine(imageDir, "json");
            string jsonFileName = System.IO.Path.GetFileNameWithoutExtension(currentImagePath) + "_mosaic.json";
            string fullJsonPath = System.IO.Path.Combine(jsonDir, jsonFileName);

            _mosaicEdges.Clear();
            _isMosaicMode = false;
            _isRegionMode = true;

            if (System.IO.File.Exists(fullJsonPath))
            {
                try
                {
                    string jsonString = System.IO.File.ReadAllText(fullJsonPath);
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var projectData = JsonSerializer.Deserialize<MosaicProjectData>(jsonString, options);

                    if (projectData != null)
                    {
                        _regionX = projectData.CropWindow.X;
                        _regionY = projectData.CropWindow.Y;

                        for (int i = 0; i < _cropSizes.Length; i++)
                        {
                            if (_cropSizes[i] == projectData.CropWindow.Size)
                            {
                                CropSizeComboBox.SelectedIndex = i;
                                break;
                            }
                        }

                        foreach (var lineData in projectData.MosaicLines)
                        {
                            _mosaicEdges.Add(new MosaicEdge(lineData.IsVertical, lineData.X, lineData.Y, lineData.Length));
                        }

                        _isRegionMode = true;
                        _isMosaicMode = false;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to load existing JSON: {ex.Message}");
                }
            }
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentImage == null || _currentIndex < 0 || _imageFiles.Count == 0) return;

            string currentImagePath = _imageFiles[_currentIndex].Path;
            string imageDir = System.IO.Path.GetDirectoryName(currentImagePath)!;
            string jsonDir = System.IO.Path.Combine(imageDir, "json");
            string jsonFileName = System.IO.Path.GetFileNameWithoutExtension(currentImagePath) + "_mosaic.json";
            string fullJsonPath = System.IO.Path.Combine(jsonDir, jsonFileName);

            if (System.IO.File.Exists(fullJsonPath))
            {
                try
                {
                    System.IO.File.SetAttributes(fullJsonPath, System.IO.FileAttributes.Normal);
                    System.IO.File.Delete(fullJsonPath);
                }
                catch (Exception ex)
                {
                    FileNameText.Text = "DELETE ERROR: " + ex.Message;
                }
            }

            _mosaicEdges.Clear();
            _isMosaicMode = false;
            _isRegionMode = true;
            UpdateRegionRectSize();
            ImageCanvas.Invalidate();
        }

        private void UpdateRegionRectSize()
        {
            if (_currentImage == null) return;

            int size = _cropSizes[CropSizeComboBox.SelectedIndex];
            double imgWidth = _currentImage.SizeInPixels.Width;
            double imgHeight = _currentImage.SizeInPixels.Height;

            if (_regionX + size > imgWidth) _regionX = Math.Max(0, imgWidth - size);
            if (_regionY + size > imgHeight) _regionY = Math.Max(0, imgHeight - size);

            ImageCanvas?.Invalidate();
        }

        private async void CropSizeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (_currentImage == null || _cropSizes == null) return;

                if (_isRegionMode)
                {
                    UpdateRegionRectSize();
                }
                else if (_isMosaicMode)
                {
                    await ZoomToFitCropBox();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Zoom error: {ex.Message}");
            }
        }

        private async void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentImage == null || _currentIndex < 0 || _imageFiles.Count == 0) return;

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

            var options = new JsonSerializerOptions { WriteIndented = true };
            string jsonString = JsonSerializer.Serialize(projectData, options);

            string currentImagePath = _imageFiles[_currentIndex].Path;
            string imageDir = System.IO.Path.GetDirectoryName(currentImagePath)!;
            string jsonDir = System.IO.Path.Combine(imageDir, "json");

            System.IO.Directory.CreateDirectory(jsonDir);

            string jsonFileName = System.IO.Path.GetFileNameWithoutExtension(projectData.ImageFileName) + "_mosaic.json";
            string fullSavePath = System.IO.Path.Combine(jsonDir, jsonFileName);

            System.IO.File.WriteAllText(fullSavePath, jsonString);

            await ExitMosaicMode();

            int nextUnannotatedIndex = -1;
            for (int i = _currentIndex + 1; i < _imageFiles.Count; i++)
            {
                string checkImgPath = _imageFiles[i].Path;
                string checkImgDir = System.IO.Path.GetDirectoryName(checkImgPath)!;
                string checkJsonDir = System.IO.Path.Combine(checkImgDir, "json");
                string checkJsonFile = System.IO.Path.GetFileNameWithoutExtension(checkImgPath) + "_mosaic.json";
                string checkFullJsonPath = System.IO.Path.Combine(checkJsonDir, checkJsonFile);

                if (!System.IO.File.Exists(checkFullJsonPath))
                {
                    nextUnannotatedIndex = i;
                    break;
                }
            }

            if (nextUnannotatedIndex != -1)
            {
                _currentIndex = nextUnannotatedIndex;
                await DisplaySelectedImageAsync();
            }
        }

        private void ContrastSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) { ImageCanvas?.Invalidate(); }
        private void ShadowSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) { ImageCanvas?.Invalidate(); }
        private void EdgeModeToggle_Click(object sender, RoutedEventArgs e) { ImageCanvas?.Invalidate(); }
        private void ImageCanvas_CreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args) { }

        // --- PURE GPU RENDER LOOP (No custom math matrix needed) ---
        private void ImageCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
        {
            if (_currentImage == null) return;

            ICanvasImage imageToDraw = _currentImage;

            if (ContrastSlider != null && ContrastSlider.Value != 1)
                imageToDraw = new ContrastEffect { Source = imageToDraw, Contrast = (float)(ContrastSlider.Value - 1) };

            if (ShadowSlider != null && ShadowSlider.Value > 0)
                imageToDraw = new HighlightsAndShadowsEffect { Source = imageToDraw, Shadows = (float)ShadowSlider.Value };

            if (EdgeModeToggle?.IsChecked == true)
                imageToDraw = new EdgeDetectionEffect { Source = imageToDraw, Amount = 1.0f, BlurAmount = 0.5f, OverlayEdges = true };

            args.DrawingSession.DrawImage(imageToDraw);

            if (_isRegionMode)
            {
                int size = _cropSizes[CropSizeComboBox.SelectedIndex];
                args.DrawingSession.FillRectangle((float)_regionX, (float)_regionY, size, size, Windows.UI.Color.FromArgb(16, 0, 255, 0));
                args.DrawingSession.DrawRectangle((float)_regionX, (float)_regionY, size, size, Microsoft.UI.Colors.Lime, 2.0f);
            }

            foreach (var edge in _mosaicEdges)
            {
                float x1 = (float)edge.X;
                float y1 = (float)edge.Y;
                float x2 = edge.IsVertical ? x1 : x1 + (float)edge.Length;
                float y2 = edge.IsVertical ? y1 + (float)edge.Length : y1;

                var color = (_activeEdge == edge || _selectedEdges.Contains(edge)) ? Microsoft.UI.Colors.Yellow : Microsoft.UI.Colors.Lime;

                args.DrawingSession.DrawLine(x1, y1, x2, y2, color, 2.0f);
                //args.DrawingSession.FillCircle(x1, y1, 4.0f, Microsoft.UI.Colors.White);
                //args.DrawingSession.FillCircle(x2, y2, 4.0f, Microsoft.UI.Colors.White);
            }

            if (_isMosaicMode)
            {
                if (_isSelectingLineEnds)
                {
                    args.DrawingSession.DrawRectangle((float)_cornerSelectionRect.X, (float)_cornerSelectionRect.Y, (float)_cornerSelectionRect.Width, (float)_cornerSelectionRect.Height, Microsoft.UI.Colors.DodgerBlue, 2.0f);
                    args.DrawingSession.FillRectangle((float)_cornerSelectionRect.X, (float)_cornerSelectionRect.Y, (float)_cornerSelectionRect.Width, (float)_cornerSelectionRect.Height, Windows.UI.Color.FromArgb(51, 30, 144, 255));
                }
            }
        }

        // --- NEW GEOMETRIC HIT-TESTING ---
        private const float HitTolerance = 10.0f;
        private (MosaicEdge? edge, DragMode mode) HitTestEdges(Windows.Foundation.Point pos)
        {
            // Dynamically scale the tolerance based on how far the image is zoomed out
            float currentZoom = ImageScrollViewer.ZoomFactor;
            float dynamicTolerance = HitTolerance / currentZoom;

            foreach (var edge in _mosaicEdges)
            {
                var startPt = new System.Numerics.Vector2((float)edge.X, (float)edge.Y);
                var endPt = edge.IsVertical
                    ? new System.Numerics.Vector2((float)edge.X, (float)edge.Y + (float)edge.Length)
                    : new System.Numerics.Vector2((float)edge.X + (float)edge.Length, (float)edge.Y);
                var mousePt = new System.Numerics.Vector2((float)pos.X, (float)pos.Y);

                // Check handles using the dynamic radius
                if (System.Numerics.Vector2.Distance(mousePt, startPt) <= dynamicTolerance) return (edge, DragMode.StartHandle);
                if (System.Numerics.Vector2.Distance(mousePt, endPt) <= dynamicTolerance) return (edge, DragMode.EndHandle);

                // Check line bodies using dynamic thickness and pad the length so you don't miss near the edges
                if (edge.IsVertical)
                {
                    if (Math.Abs(pos.X - edge.X) <= dynamicTolerance && pos.Y >= edge.Y - dynamicTolerance && pos.Y <= edge.Y + edge.Length + dynamicTolerance)
                        return (edge, DragMode.Body);
                }
                else
                {
                    if (Math.Abs(pos.Y - edge.Y) <= dynamicTolerance && pos.X >= edge.X - dynamicTolerance && pos.X <= edge.X + edge.Length + dynamicTolerance)
                        return (edge, DragMode.Body);
                }
            }
            return (null, DragMode.None);
        }

        private async void OverlayCanvas_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(OverlayCanvas);

            // ADD THIS: Start panning on middle mouse button
            if (point.Properties.IsMiddleButtonPressed)
            {
                _isPanning = true;
                _lastPanScreenPos = e.GetCurrentPoint(null).Position;
                OverlayCanvas.CapturePointer(e.Pointer);
                e.Handled = true;
                return;
            }

            var pos = point.Position; // Automatically mapped to image coordinates by WinUI 3

            if (_isMosaicMode)
            {
                var shiftState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift);
                var ctrlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
                bool isShiftPressed = (shiftState & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
                bool isCtrlPressed = (ctrlState & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

                if (point.Properties.IsLeftButtonPressed && isShiftPressed)
                {
                    _isSelectingLineEnds = true;
                    _selectionStartPos = pos;
                    _cornerSelectionRect = new Windows.Foundation.Rect(pos.X, pos.Y, 0, 0);
                    OverlayCanvas.CapturePointer(e.Pointer);
                    e.Handled = true;
                    return;
                }

                var hitResult = HitTestEdges(pos);

                if (point.Properties.IsRightButtonPressed)
                {
                    var flyout = new Microsoft.UI.Xaml.Controls.MenuFlyout();

                    if (hitResult.edge != null)
                    {
                        // Ensure the right-clicked item is part of the selection
                        if (!_selectedEdges.Contains(hitResult.edge))
                        {
                            _selectedEdges.Clear();
                            _selectedEdges.Add(hitResult.edge);
                            ImageCanvas.Invalidate();
                        }

                        if (_selectedEdges.Count == 1)
                        {
                            var toggleItem = new MenuFlyoutItem { Text = "Toggle Orientation" };
                            toggleItem.Click += (s, args) => { hitResult.edge.ToggleOrientation(); ImageCanvas.Invalidate(); };
                            flyout.Items.Add(toggleItem);
                        }

                        var duplicateItem = new MenuFlyoutItem { Text = $"Duplicate {_selectedEdges.Count} Lines" };
                        duplicateItem.Click += (s, args) =>
                        {
                            var newSelection = new HashSet<MosaicEdge>();

                            // Group selected lines by orientation and sort them to analyze the pattern
                            var vSelected = _selectedEdges.Where(e => e.IsVertical).OrderBy(e => e.X).ToList();
                            var hSelected = _selectedEdges.Where(e => !e.IsVertical).OrderBy(e => e.Y).ToList();

                            double GetSmartOffset(List<MosaicEdge> selected, bool isVert)
                            {
                                if (selected.Count >= 2)
                                {
                                    // Multi-select: Calculate average gap. Offset the whole block by (Gap * Count).
                                    double min = isVert ? selected.First().X : selected.First().Y;
                                    double max = isVert ? selected.Last().X : selected.Last().Y;
                                    double avgGap = (max - min) / (selected.Count - 1);
                                    double offset = avgGap * selected.Count;
                                    return offset > 0 ? offset : 20.0;
                                }
                                else if (selected.Count == 1)
                                {
                                    // Single-select: Find the closest adjacent line in the global list
                                    var globalEdges = _mosaicEdges.Where(e => e.IsVertical == isVert).OrderBy(e => isVert ? e.X : e.Y).ToList();
                                    if (globalEdges.Count >= 2)
                                    {
                                        int idx = globalEdges.IndexOf(selected[0]);
                                        double offset = 0;

                                        // Prefer spacing to the right/bottom, otherwise use left/top
                                        if (idx >= 0 && idx < globalEdges.Count - 1)
                                            offset = isVert ? globalEdges[idx + 1].X - globalEdges[idx].X : globalEdges[idx + 1].Y - globalEdges[idx].Y;
                                        else if (idx > 0)
                                            offset = isVert ? globalEdges[idx].X - globalEdges[idx - 1].X : globalEdges[idx].Y - globalEdges[idx - 1].Y;

                                        if (offset > 0) return offset;
                                    }
                                }
                                return 20.0; // Fallback if no pattern exists
                            }

                            double vOffset = GetSmartOffset(vSelected, true);
                            double hOffset = GetSmartOffset(hSelected, false);

                            foreach (var edge in _selectedEdges)
                            {
                                double newX = edge.IsVertical ? edge.X + vOffset : edge.X;
                                double newY = edge.IsVertical ? edge.Y : edge.Y + hOffset;

                                var newEdge = new MosaicEdge(edge.IsVertical, newX, newY, edge.Length);
                                _mosaicEdges.Add(newEdge);
                                newSelection.Add(newEdge);
                            }

                            _selectedEdges = newSelection;
                            ImageCanvas.Invalidate();
                        };

                        flyout.Items.Add(duplicateItem);

                        var deleteItem = new MenuFlyoutItem { Text = $"Delete {_selectedEdges.Count} Lines" };
                        deleteItem.Click += (s, args) =>
                        {
                            foreach (var edge in _selectedEdges) _mosaicEdges.Remove(edge);
                            _selectedEdges.Clear();
                            ImageCanvas.Invalidate();
                        };
                        flyout.Items.Add(deleteItem);
                    }
                    else
                    {
                        var vItem = new MenuFlyoutItem { Text = "Add Vertical Line" };
                        vItem.Click += (s, args) => { _mosaicEdges.Add(new MosaicEdge(true, pos.X, pos.Y, 100)); ImageCanvas.Invalidate(); };
                        flyout.Items.Add(vItem);

                        var hItem = new MenuFlyoutItem { Text = "Add Horizontal Line" };
                        hItem.Click += (s, args) => { _mosaicEdges.Add(new MosaicEdge(false, pos.X, pos.Y, 100)); ImageCanvas.Invalidate(); };
                        flyout.Items.Add(hItem);
                    }

                    flyout.ShowAt(OverlayCanvas, point.Position);
                    e.Handled = true;
                    return;
                }

                if (point.Properties.IsLeftButtonPressed)
                {
                    if (hitResult.edge != null)
                    {
                        if (isCtrlPressed)
                        {
                            if (_selectedEdges.Contains(hitResult.edge)) _selectedEdges.Remove(hitResult.edge);
                            else _selectedEdges.Add(hitResult.edge);
                        }
                        else
                        {
                            if (!_selectedEdges.Contains(hitResult.edge))
                            {
                                _selectedEdges.Clear();
                                _selectedEdges.Add(hitResult.edge);
                            }
                            _activeEdge = hitResult.edge;
                            _dragMode = hitResult.mode;
                            _lastMousePos = pos;
                            OverlayCanvas.CapturePointer(e.Pointer);
                        }
                        ImageCanvas.Invalidate();
                        e.Handled = true;
                    }
                    else if (!isCtrlPressed)
                    {
                        // Clear selection if clicking empty space without CTRL
                        _selectedEdges.Clear();
                        ImageCanvas.Invalidate();
                    }
                }
                return;
            }

            if (!_isRegionMode) return;
            int size = _cropSizes[CropSizeComboBox.SelectedIndex];
            bool isInside = pos.X >= _regionX && pos.X <= _regionX + size && pos.Y >= _regionY && pos.Y <= _regionY + size;

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
                EnterMosaicMode();
                await ZoomToFitCropBox();
            }
        }

        private void OverlayCanvas_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            // ADD THIS: Drive the ScrollViewer in real-time
            if (_isPanning)
            {
                var screenPos = e.GetCurrentPoint(null).Position;
                double dx = _lastPanScreenPos.X - screenPos.X;
                double dy = _lastPanScreenPos.Y - screenPos.Y;

                // The 'true' parameter disables the slide animation for perfect 1:1 mouse sync
                ImageScrollViewer.ChangeView(
                    ImageScrollViewer.HorizontalOffset + dx,
                    ImageScrollViewer.VerticalOffset + dy,
                    null,
                    true);

                _lastPanScreenPos = screenPos;
                return;
            }

            var point = e.GetCurrentPoint(OverlayCanvas);
            var pos = point.Position;

            if (_isSelectingLineEnds)
            {
                double x = Math.Min(pos.X, _selectionStartPos.X);
                double y = Math.Min(pos.Y, _selectionStartPos.Y);
                double w = Math.Abs(pos.X - _selectionStartPos.X);
                double h = Math.Abs(pos.Y - _selectionStartPos.Y);
                _cornerSelectionRect = new Windows.Foundation.Rect(x, y, w, h);
                ImageCanvas.Invalidate();
                return;
            }

            if (_isMosaicMode && _activeEdge != null)
            {
                double dx = pos.X - _lastMousePos.X;
                double dy = pos.Y - _lastMousePos.Y;

                // If dragging the body, move ALL selected edges together
                if (_dragMode == DragMode.Body)
                {
                    foreach (var edge in _selectedEdges) edge.Move(dx, dy);
                }
                else if (_dragMode == DragMode.StartHandle) _activeEdge.ResizeStart(dx, dy);
                else if (_dragMode == DragMode.EndHandle) _activeEdge.ResizeEnd(dx, dy);

                _lastMousePos = pos;
                ImageCanvas.Invalidate();
                return;
            }

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

                _lastMousePos = pos;
                ImageCanvas.Invalidate();
            }
        }

        private void OverlayCanvas_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            // ADD THIS: Stop panning
            if (_isPanning)
            {
                _isPanning = false;
                OverlayCanvas.ReleasePointerCapture(e.Pointer);
                return;
            }

            if (_isSelectingLineEnds)
            {
                _isSelectingLineEnds = false;
                OverlayCanvas.ReleasePointerCapture(e.Pointer);

                double rectX = _cornerSelectionRect.X;
                double rectY = _cornerSelectionRect.Y;
                double rectW = _cornerSelectionRect.Width;
                double rectH = _cornerSelectionRect.Height;

                var vEdgesInBox = new List<MosaicEdge>();
                var hEdgesInBox = new List<MosaicEdge>();

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

                foreach (var h in _mosaicEdges.Where(edge => !edge.IsVertical))
                {
                    bool startInBox = h.X >= rectX && h.X <= rectX + rectW && h.Y >= rectY && h.Y <= rectY + rectH;
                    if (startInBox && vEdgesInBox.Count > 0)
                        h.SnapStart(vEdgesInBox.OrderBy(v => Math.Abs(v.X - h.X)).First().X);

                    double endX = h.X + h.Length;
                    bool endInBox = endX >= rectX && endX <= rectX + rectW && h.Y >= rectY && h.Y <= rectY + rectH;
                    if (endInBox && vEdgesInBox.Count > 0)
                        h.SnapEnd(vEdgesInBox.OrderBy(v => Math.Abs(v.X - endX)).First().X);
                }

                foreach (var v in _mosaicEdges.Where(edge => edge.IsVertical))
                {
                    bool startInBox = v.X >= rectX && v.X <= rectX + rectW && v.Y >= rectY && v.Y <= rectY + rectH;
                    if (startInBox && hEdgesInBox.Count > 0)
                        v.SnapStart(hEdgesInBox.OrderBy(h => Math.Abs(h.Y - v.Y)).First().Y);

                    double endY = v.Y + v.Length;
                    bool endInBox = v.X >= rectX && v.X <= rectX + rectW && endY >= rectY && endY <= rectY + rectH;
                    if (endInBox && hEdgesInBox.Count > 0)
                        v.SnapEnd(hEdgesInBox.OrderBy(h => Math.Abs(h.Y - endY)).First().Y);
                }

                ImageCanvas.Invalidate();
                return;
            }

            if (_isMosaicMode && _activeEdge != null)
            {
                _activeEdge = null;
                _dragMode = DragMode.None;
                OverlayCanvas.ReleasePointerCapture(e.Pointer);
                ImageCanvas.Invalidate();
                return;
            }

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

                if (delta > 0 && CropSizeComboBox.SelectedIndex < _cropSizes.Length - 1)
                    CropSizeComboBox.SelectedIndex++;
                else if (delta < 0 && CropSizeComboBox.SelectedIndex > 0)
                    CropSizeComboBox.SelectedIndex--;

                e.Handled = true;
            }
            // By NOT setting e.Handled = true when outside the box, the normal ScrollViewer scroll/zoom still works natively!
        }

        private async void RootGrid_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Escape && _isMosaicMode)
            {
                e.Handled = true;
                await ExitMosaicMode();
            }
        }

        private void EnterMosaicMode()
        {
            if (_currentImage == null) return;

            int size = _cropSizes[CropSizeComboBox.SelectedIndex];

            // Re-enable the Clip geometry that perfectly matches the cropped area
            ImageCanvas.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(_regionX, _regionY, size, size)
            };

            PrevButton.IsEnabled = false;
            NextButton.IsEnabled = false;
            LoadDirectoryButton.IsEnabled = false;
            ClearButton.IsEnabled = false;
        }

        private async Task ExitMosaicMode()
        {
            if (_currentImage == null) return;

            _isMosaicMode = false;
            _isRegionMode = true;

            ImageCanvas.Clip = null;

            PrevButton.IsEnabled = _currentIndex > 0;
            NextButton.IsEnabled = _currentIndex < _imageFiles.Count - 1;
            LoadDirectoryButton.IsEnabled = true;
            ClearButton.IsEnabled = true;

            await ZoomToFitImage();
        }

        private async Task ZoomToFitCropBox()
        {
            if (_currentImage == null || _cropSizes == null || ImageScrollViewer.ViewportWidth == 0) return;

            double cropSize = _cropSizes[CropSizeComboBox.SelectedIndex];

            ImageWorkArea.HorizontalAlignment = HorizontalAlignment.Left;
            ImageWorkArea.VerticalAlignment = VerticalAlignment.Top;

            double marginX = ImageScrollViewer.ViewportWidth / 2.0;
            double marginY = ImageScrollViewer.ViewportHeight / 2.0;
            ImageWorkArea.Margin = new Thickness(marginX, marginY, marginX, marginY);

            ImageWorkArea.UpdateLayout();
            ImageScrollViewer.UpdateLayout();

            await Task.Delay(50);

            double scaleX = ImageScrollViewer.ViewportWidth / cropSize;
            double scaleY = ImageScrollViewer.ViewportHeight / cropSize;
            float targetZoom = (float)Math.Min(scaleX, scaleY) * 0.90f;

            double cropCenterX = _regionX + (cropSize / 2.0);
            double cropCenterY = _regionY + (cropSize / 2.0);

            double targetX = (marginX + cropCenterX) * targetZoom;
            double targetY = (marginY + cropCenterY) * targetZoom;

            double hOffset = targetX - (ImageScrollViewer.ViewportWidth / 2.0);
            double vOffset = targetY - (ImageScrollViewer.ViewportHeight / 2.0);

            ImageScrollViewer.ChangeView(hOffset, vOffset, targetZoom);
        }

        private async Task ZoomToFitImage()
        {
            if (_currentImage == null || ImageScrollViewer.ViewportWidth == 0) return;

            ImageWorkArea.HorizontalAlignment = HorizontalAlignment.Center;
            ImageWorkArea.VerticalAlignment = VerticalAlignment.Center;
            ImageWorkArea.Margin = new Thickness(0);

            ImageWorkArea.UpdateLayout();
            ImageScrollViewer.UpdateLayout();

            await Task.Delay(50);

            double scaleX = ImageScrollViewer.ViewportWidth / _currentImage.SizeInPixels.Width;
            double scaleY = ImageScrollViewer.ViewportHeight / _currentImage.SizeInPixels.Height;
            float zoomFactor = (float)Math.Min(scaleX, scaleY) * 0.95f;

            ImageScrollViewer.ChangeView(null, null, zoomFactor);
        }
    }

    public static class NativeInterop
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        public struct COMDLG_FILTERSPEC
        {
            public string pszName;
            public string pszSpec;
        }

        [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
        private class FileOpenDialog { }

        [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("d57c7288-d4ad-4768-be02-9d969532d960"), System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileDialog
        {
            [System.Runtime.InteropServices.PreserveSig] int Show([System.Runtime.InteropServices.In] IntPtr parent);
            void SetFileTypes([System.Runtime.InteropServices.In] uint cFileTypes, [System.Runtime.InteropServices.In, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPArray)] COMDLG_FILTERSPEC[] rgFilterSpec);
            void SetFileTypeIndex([System.Runtime.InteropServices.In] uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise([System.Runtime.InteropServices.In, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Interface)] object pfde, out uint pdwCookie);
            void Unadvise([System.Runtime.InteropServices.In] uint dwCookie);
            void SetOptions([System.Runtime.InteropServices.In] uint fos);
            void GetOptions(out uint pfos);
            void SetDefaultFolder([System.Runtime.InteropServices.In, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Interface)] object psi);
            void SetFolder([System.Runtime.InteropServices.In, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Interface)] object psi);
            void GetFolder([System.Runtime.InteropServices.Out, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Interface)] out object ppsi);
            void GetCurrentSelection([System.Runtime.InteropServices.Out, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Interface)] out object ppsi);
            void SetFileName([System.Runtime.InteropServices.In, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszName);
            void GetFileName([System.Runtime.InteropServices.Out, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([System.Runtime.InteropServices.In, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([System.Runtime.InteropServices.In, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([System.Runtime.InteropServices.In, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszLabel);
            void GetResult([System.Runtime.InteropServices.Out, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Interface)] out IShellItem ppsi);
            void AddPlace([System.Runtime.InteropServices.In, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Interface)] object psi, int fdap);
            void SetDefaultExtension([System.Runtime.InteropServices.In, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close([System.Runtime.InteropServices.In] int hr);
            void SetClientGuid([System.Runtime.InteropServices.In] ref Guid guid);
            void ClearClientData();
            void SetFilter([System.Runtime.InteropServices.In] IntPtr pFilter);
        }

        [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler([System.Runtime.InteropServices.In] IntPtr pbc, [System.Runtime.InteropServices.In] ref Guid bhid, [System.Runtime.InteropServices.In] ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName([System.Runtime.InteropServices.In] int sigdnName, [System.Runtime.InteropServices.Out, System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] out string ppszName);
            void GetAttributes([System.Runtime.InteropServices.In] uint sfgaoMask, out uint psfgaoAttribs);
            void Compare([System.Runtime.InteropServices.In] IShellItem psi, [System.Runtime.InteropServices.In] uint hint, out int piOrder);
        }

        public static string? PickFolder(IntPtr hwnd)
        {
            var dialog = (IFileDialog)new FileOpenDialog();
            dialog.GetOptions(out uint options);
            dialog.SetOptions(options | 0x00000020);

            if (dialog.Show(hwnd) == 0)
            {
                dialog.GetResult(out IShellItem item);
                item.GetDisplayName(unchecked((int)0x80058000), out string path);
                return path;
            }
            return null;
        }

        public static string? PickFile(IntPtr hwnd)
        {
            var dialog = (IFileDialog)new FileOpenDialog();

            var extensions = new COMDLG_FILTERSPEC[]
            {
            new COMDLG_FILTERSPEC { pszName = "Images", pszSpec = "*.png;*.jpg;*.jpeg" },
            new COMDLG_FILTERSPEC { pszName = "All Files", pszSpec = "*.*" }
            };
            dialog.SetFileTypes((uint)extensions.Length, extensions);

            dialog.GetOptions(out uint options);
            dialog.SetOptions(options | 0x00000040);

            if (dialog.Show(hwnd) == 0)
            {
                dialog.GetResult(out IShellItem item);
                item.GetDisplayName(unchecked((int)0x80058000), out string path);
                return path;
            }
            return null;
        }
    }
}