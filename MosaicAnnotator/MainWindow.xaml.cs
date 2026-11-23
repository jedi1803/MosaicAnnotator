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
        public int TotalRectangles { get; set; }
        public CropWindowData CropWindow { get; set; } = new CropWindowData();
        public List<MosaicLineData> MosaicLines { get; set; } = new List<MosaicLineData>();
    }

    public class MosaicMark
    {
        public double X { get; set; }
        public double Y { get; set; }
        public MosaicMark(double x, double y) { X = x; Y = y; }
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

        private List<string> _regionJsonPaths = new List<string>();
        private Dictionary<string, CropWindowData> _savedRegionBoxes = new Dictionary<string, CropWindowData>();
        private string _currentJsonPath = string.Empty;
        private bool _isUpdatingRegionCombo = false;

        private List<MosaicEdge> _mosaicEdges = new List<MosaicEdge>();
        private MosaicEdge? _activeEdge = null;
        private List<MosaicEdge> _errorEdges = new List<MosaicEdge>();

        private List<MosaicMark> _mosaicMarks = new List<MosaicMark>();
        private MosaicMark? _activeMark = null;

        private Stack<Action> _undoStack = new Stack<Action>();

        private enum DragMode { None, Body, StartHandle, EndHandle }
        private DragMode _dragMode = DragMode.None;

        private bool _isSelectingLineEnds = false;
        private Windows.Foundation.Point _selectionStartPos;
        private Windows.Foundation.Rect _cornerSelectionRect;

        private bool _isPanning = false;
        private Windows.Foundation.Point _lastPanScreenPos;

        // NEW: Variables to track the intended offset without waiting for the UI to catch up
        private double _panTargetX;
        private double _panTargetY;

        public MainWindow()
        {
            this.InitializeComponent();
        }

        private void RootGrid_Loaded(object sender, RoutedEventArgs e)
        {
            IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            Microsoft.UI.WindowId windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            Microsoft.UI.Windowing.AppWindow appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

            // Increased to 1800 physical pixels to account for Windows UI scaling
            appWindow.Resize(new Windows.Graphics.SizeInt32(1800, 900));
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
            string baseName = System.IO.Path.GetFileNameWithoutExtension(currentImagePath);

            _regionJsonPaths.Clear();
            _savedRegionBoxes.Clear();

            if (System.IO.Directory.Exists(jsonDir))
            {
                string primaryPath = System.IO.Path.Combine(jsonDir, $"{baseName}_mosaic.json");
                if (System.IO.File.Exists(primaryPath))
                    _regionJsonPaths.Add(primaryPath);

                var extraFiles = System.IO.Directory.GetFiles(jsonDir, $"{baseName}_mosaic_*.json")
                    .OrderBy(f =>
                    {
                        string name = System.IO.Path.GetFileNameWithoutExtension(f);
                        string suffixStr = name.Substring((baseName + "_mosaic_").Length);
                        return int.TryParse(suffixStr, out int n) ? n : int.MaxValue;
                    });

                _regionJsonPaths.AddRange(extraFiles);
            }

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            foreach (var path in _regionJsonPaths)
            {
                try
                {
                    string jsonString = System.IO.File.ReadAllText(path);
                    var projectData = JsonSerializer.Deserialize<MosaicProjectData>(jsonString, options);
                    if (projectData != null)
                        _savedRegionBoxes[path] = projectData.CropWindow;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to cache region box for {path}: {ex.Message}");
                }
            }

            _isUpdatingRegionCombo = true;
            RegionComboBox.Items.Clear();

            if (_regionJsonPaths.Count == 0)
            {
                _regionJsonPaths.Add(string.Empty);
                RegionComboBox.Items.Add("Region 1");
            }
            else
            {
                for (int i = 0; i < _regionJsonPaths.Count; i++)
                    RegionComboBox.Items.Add($"Region {i + 1}");
            }

            RegionComboBox.SelectedIndex = 0;
            _isUpdatingRegionCombo = false;

            LoadRegionFromPath(_regionJsonPaths[0]);
        }

        private void LoadRegionFromPath(string fullJsonPath)
        {
            _currentJsonPath = fullJsonPath;
            _mosaicEdges.Clear();
            _mosaicMarks.Clear();
            _undoStack.Clear();
            _errorEdges.Clear();
            _isMosaicMode = false;
            _isRegionMode = true;

            if (!string.IsNullOrEmpty(fullJsonPath) && System.IO.File.Exists(fullJsonPath))
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
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to load existing JSON: {ex.Message}");
                }
            }

            UpdateRegionRectSize();
            ImageCanvas?.Invalidate();
        }

        private void RegionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isUpdatingRegionCombo || _currentImage == null) return;
            int idx = RegionComboBox.SelectedIndex;
            if (idx >= 0 && idx < _regionJsonPaths.Count)
            {
                LoadRegionFromPath(_regionJsonPaths[idx]);
            }
        }

        private void NewRegionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentImage == null || _isMosaicMode) return;

            // If an unsaved new region already exists, just select it
            int unsavedIdx = _regionJsonPaths.IndexOf(string.Empty);
            if (unsavedIdx != -1)
            {
                RegionComboBox.SelectedIndex = unsavedIdx;
                return;
            }

            _regionJsonPaths.Add(string.Empty);
            _isUpdatingRegionCombo = true;
            RegionComboBox.Items.Add($"Region {_regionJsonPaths.Count} (New)");
            RegionComboBox.SelectedIndex = _regionJsonPaths.Count - 1;
            _isUpdatingRegionCombo = false;

            LoadRegionFromPath(string.Empty);
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentImage == null || _currentIndex < 0 || _imageFiles.Count == 0) return;

            if (!string.IsNullOrEmpty(_currentJsonPath) && System.IO.File.Exists(_currentJsonPath))
            {
                try
                {
                    System.IO.File.SetAttributes(_currentJsonPath, System.IO.FileAttributes.Normal);
                    System.IO.File.Delete(_currentJsonPath);
                }
                catch (Exception ex)
                {
                    FileNameText.Text = "DELETE ERROR: " + ex.Message;
                }
            }

            LoadExistingJsonData();
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

            // --- BATCH OVERLAP VALIDATION ---
            _errorEdges.Clear();
            double tolerance = 2.0;

            for (int i = 0; i < _mosaicEdges.Count; i++)
            {
                for (int j = i + 1; j < _mosaicEdges.Count; j++)
                {
                    var e1 = _mosaicEdges[i];
                    var e2 = _mosaicEdges[j];

                    if (e1.IsVertical != e2.IsVertical) continue;

                    bool isOverlap = e1.IsVertical
                        ? (Math.Abs(e1.X - e2.X) < tolerance && Math.Max(e1.Y, e2.Y) < Math.Min(e1.Y + e1.Length, e2.Y + e2.Length))
                        : (Math.Abs(e1.Y - e2.Y) < tolerance && Math.Max(e1.X, e2.X) < Math.Min(e1.X + e1.Length, e2.X + e2.Length));

                    if (isOverlap)
                    {
                        if (!_errorEdges.Contains(e1)) _errorEdges.Add(e1);
                        if (!_errorEdges.Contains(e2)) _errorEdges.Add(e2);
                    }
                }
            }

            if (_errorEdges.Count > 0)
            {
                ImageCanvas.Invalidate();

                var dialog = new ContentDialog
                {
                    Title = "Overlaps Detected",
                    Content = $"Found {_errorEdges.Count} overlapping lines (highlighted in red). Please delete the duplicates before exporting.",
                    CloseButtonText = "OK",
                    XamlRoot = this.Content.XamlRoot
                };
                await dialog.ShowAsync();
                return; // Abort export
            }
            // --- END OVERLAP VALIDATION ---

            var projectData = new MosaicProjectData
            {
                ImageFileName = _imageFiles[_currentIndex].Name,
                TotalRectangles = CountMosaicRectangles(),
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

            string fullSavePath;
            if (!string.IsNullOrEmpty(_currentJsonPath))
            {
                fullSavePath = _currentJsonPath;
            }
            else
            {
                string baseName = System.IO.Path.GetFileNameWithoutExtension(projectData.ImageFileName);
                fullSavePath = System.IO.Path.Combine(jsonDir, $"{baseName}_mosaic.json");

                int suffix = 2;
                while (System.IO.File.Exists(fullSavePath))
                {
                    fullSavePath = System.IO.Path.Combine(jsonDir, $"{baseName}_mosaic_{suffix}.json");
                    suffix++;
                }
            }

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
            else
            {
                LoadExistingJsonData();
            }
        }

        private void ContrastSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) { ImageCanvas?.Invalidate(); }
        private void ShadowSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) { ImageCanvas?.Invalidate(); }
        private void Filter_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) { ImageCanvas?.Invalidate(); }
        private void Filter_Click(object sender, RoutedEventArgs e) { ImageCanvas?.Invalidate(); }
        private void ImageCanvas_CreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args) { }

        // --- PURE GPU RENDER LOOP (No custom math matrix needed) ---
        private void ImageCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
        {
            if (_currentImage == null) return;

            ICanvasImage imageToDraw = _currentImage;

            // 1. Base Contrast (Existing)
            if (ContrastSlider != null && ContrastSlider.Value != 1)
                imageToDraw = new ContrastEffect { Source = imageToDraw, Contrast = (float)(ContrastSlider.Value - 1) };

            // 2. Shadows (Existing)
            if (ShadowSlider != null && ShadowSlider.Value > 0)
                imageToDraw = new HighlightsAndShadowsEffect { Source = imageToDraw, Shadows = (float)ShadowSlider.Value };

            // 3. NEW: Sharpen Effect (Amplifies the harsh boundaries of mosaic tiles)
            if (SharpenSlider != null && SharpenSlider.Value > 0)
                imageToDraw = new SharpenEffect { Source = imageToDraw, Amount = (float)SharpenSlider.Value };

            // 4. NEW: Emboss Effect (Turns grid lines into 3D stamped ridges)
            if (EmbossToggle?.IsChecked == true)
                imageToDraw = new EmbossEffect { Source = imageToDraw, Amount = 5.0f, Angle = 0.785f }; // 45-degree angle

            args.DrawingSession.DrawImage(imageToDraw);

            // Calculate dynamic thickness based on zoom
            float currentZoom = ImageScrollViewer.ZoomFactor;
            float dynamicThickness = 2.0f / currentZoom;
            float markRadius = 5.0f / currentZoom;

            if (_isRegionMode)
            {
                // Draw all other saved regions first
                foreach (var kvp in _savedRegionBoxes)
                {
                    if (kvp.Key == _currentJsonPath) continue;
                    var box = kvp.Value;
                    args.DrawingSession.FillRectangle((float)box.X, (float)box.Y, box.Size, box.Size, Windows.UI.Color.FromArgb(16, 0, 255, 255));
                    args.DrawingSession.DrawRectangle((float)box.X, (float)box.Y, box.Size, box.Size, Microsoft.UI.Colors.Cyan, dynamicThickness);
                }

                // Draw the currently active region on top
                int size = _cropSizes[CropSizeComboBox.SelectedIndex];
                args.DrawingSession.FillRectangle((float)_regionX, (float)_regionY, size, size, Windows.UI.Color.FromArgb(16, 0, 255, 0));
                args.DrawingSession.DrawRectangle((float)_regionX, (float)_regionY, size, size, Microsoft.UI.Colors.Lime, dynamicThickness);
            }

            foreach (var edge in _mosaicEdges)
            {
                float x1 = (float)edge.X;
                float y1 = (float)edge.Y;
                float x2 = edge.IsVertical ? x1 : x1 + (float)edge.Length;
                float y2 = edge.IsVertical ? y1 + (float)edge.Length : y1;

                var color = (_activeEdge == edge || _errorEdges.Contains(edge)) ? Microsoft.UI.Colors.Red : Microsoft.UI.Colors.Lime;

                args.DrawingSession.DrawLine(x1, y1, x2, y2, color, dynamicThickness);
            }

            foreach (var mark in _mosaicMarks)
            {
                var markColor = (_activeMark == mark) ? Microsoft.UI.Colors.Yellow : Microsoft.UI.Colors.Cyan;
                float mx = (float)mark.X;
                float my = (float)mark.Y;

                // Draw the 'x'
                args.DrawingSession.DrawLine(mx - markRadius, my - markRadius, mx + markRadius, my + markRadius, markColor, dynamicThickness);
                args.DrawingSession.DrawLine(mx - markRadius, my + markRadius, mx + markRadius, my - markRadius, markColor, dynamicThickness);
            }

            if (_isMosaicMode)
            {
                if (_isSelectingLineEnds)
                {
                    args.DrawingSession.DrawRectangle((float)_cornerSelectionRect.X, (float)_cornerSelectionRect.Y, (float)_cornerSelectionRect.Width, (float)_cornerSelectionRect.Height, Microsoft.UI.Colors.DodgerBlue, dynamicThickness);
                    args.DrawingSession.FillRectangle((float)_cornerSelectionRect.X, (float)_cornerSelectionRect.Y, (float)_cornerSelectionRect.Width, (float)_cornerSelectionRect.Height, Windows.UI.Color.FromArgb(51, 30, 144, 255));
                }
            }
        }

        private const float HitTolerance = 10.0f;

        private MosaicMark? HitTestMarks(Windows.Foundation.Point pos)
        {
            float currentZoom = ImageScrollViewer.ZoomFactor;
            float dynamicTolerance = HitTolerance / currentZoom;

            foreach (var mark in _mosaicMarks)
            {
                if (Math.Abs(pos.X - mark.X) <= dynamicTolerance && Math.Abs(pos.Y - mark.Y) <= dynamicTolerance)
                    return mark;
            }
            return null;
        }

        private (MosaicEdge? edge, DragMode mode) HitTestEdges(Windows.Foundation.Point pos)
        {
            float currentZoom = ImageScrollViewer.ZoomFactor;
            float dynamicTolerance = HitTolerance / currentZoom;

            foreach (var edge in _mosaicEdges)
            {
                if (edge.IsVertical)
                {
                    if (Math.Abs(pos.X - edge.X) <= dynamicTolerance && pos.Y >= edge.Y - dynamicTolerance && pos.Y <= edge.Y + edge.Length + dynamicTolerance)
                    {
                        double relativeY = pos.Y - edge.Y;
                        if (relativeY < edge.Length * 0.25) return (edge, DragMode.StartHandle);
                        if (relativeY > edge.Length * 0.75) return (edge, DragMode.EndHandle);
                        return (edge, DragMode.Body);
                    }
                }
                else
                {
                    if (Math.Abs(pos.Y - edge.Y) <= dynamicTolerance && pos.X >= edge.X - dynamicTolerance && pos.X <= edge.X + edge.Length + dynamicTolerance)
                    {
                        double relativeX = pos.X - edge.X;
                        if (relativeX < edge.Length * 0.25) return (edge, DragMode.StartHandle);
                        if (relativeX > edge.Length * 0.75) return (edge, DragMode.EndHandle);
                        return (edge, DragMode.Body);
                    }
                }
            }

            return (null, DragMode.None);
        }

        private void OverlayCanvas_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_errorEdges.Count > 0)
            {
                _errorEdges.Clear();
                ImageCanvas.Invalidate();
            }

            var point = e.GetCurrentPoint(OverlayCanvas);

            if (point.Properties.IsMiddleButtonPressed)
            {
                _isPanning = true;
                _lastPanScreenPos = e.GetCurrentPoint(null).Position;

                // Set our custom targets to the initial starting position
                _panTargetX = ImageScrollViewer.HorizontalOffset;
                _panTargetY = ImageScrollViewer.VerticalOffset;

                OverlayCanvas.CapturePointer(e.Pointer);
                e.Handled = true;
                return;
            }

            var pos = point.Position;

            if (_isMosaicMode)
            {
                // Direct hardware read
                bool isShiftPressed = NativeInterop.IsShiftPressed();
                bool isCtrlPressed = NativeInterop.IsCtrlPressed();

                if (point.Properties.IsLeftButtonPressed && isShiftPressed)
                {
                    _isSelectingLineEnds = true;
                    _selectionStartPos = pos;
                    _cornerSelectionRect = new Windows.Foundation.Rect(pos.X, pos.Y, 0, 0);
                    OverlayCanvas.CapturePointer(e.Pointer);
                    e.Handled = true;
                    return;
                }

                // Ctrl + Left Click = Place 'x' mark
                if (point.Properties.IsLeftButtonPressed && isCtrlPressed)
                {
                    _mosaicMarks.Add(new MosaicMark(pos.X, pos.Y));
                    ImageCanvas.Invalidate();
                    e.Handled = true;
                    return;
                }

                var hitMark = HitTestMarks(pos);
                var hitResult = HitTestEdges(pos);

                if (point.Properties.IsRightButtonPressed)
                {
                    var flyout = new Microsoft.UI.Xaml.Controls.MenuFlyout();

                    if (hitMark != null)
                    {
                        var hLineItem = new MenuFlyoutItem { Text = "Create Short H-Line Here" };
                        hLineItem.Click += (s, args) =>
                        {
                            var newEdge = new MosaicEdge(false, hitMark.X - 25, hitMark.Y, 50);
                            _mosaicEdges.Add(newEdge);
                            _mosaicMarks.Remove(hitMark);
                            _undoStack.Push(() => { _mosaicEdges.Remove(newEdge); _mosaicMarks.Add(hitMark); });
                            ImageCanvas.Invalidate();
                        };
                        flyout.Items.Add(hLineItem);

                        var vLineItem = new MenuFlyoutItem { Text = "Create Short V-Line Here" };
                        vLineItem.Click += (s, args) =>
                        {
                            var newEdge = new MosaicEdge(true, hitMark.X, hitMark.Y - 25, 50);
                            _mosaicEdges.Add(newEdge);
                            _mosaicMarks.Remove(hitMark);
                            _undoStack.Push(() => { _mosaicEdges.Remove(newEdge); _mosaicMarks.Add(hitMark); });
                            ImageCanvas.Invalidate();
                        };
                        flyout.Items.Add(vLineItem);

                        var delItem = new MenuFlyoutItem { Text = "Delete Mark" };
                        delItem.Click += (s, args) => { _mosaicMarks.Remove(hitMark); ImageCanvas.Invalidate(); };
                        flyout.Items.Add(delItem);
                    }
                    else if (hitResult.edge != null)
                    {
                        var clickedEdge = hitResult.edge;
                        _activeEdge = clickedEdge;
                        ImageCanvas.Invalidate();

                        flyout.Closed += (s, args) =>
                        {
                            if (_activeEdge == clickedEdge)
                            {
                                _activeEdge = null;
                                ImageCanvas.Invalidate();
                            }
                        };

                        var toggleItem = new MenuFlyoutItem { Text = "Toggle Orientation" };
                        toggleItem.Click += (s, args) => { clickedEdge.ToggleOrientation(); ImageCanvas.Invalidate(); };
                        flyout.Items.Add(toggleItem);

                        var duplicateItem = new MenuFlyoutItem { Text = "Duplicate Line" };
                        duplicateItem.Click += (s, args) =>
                        {
                            var globalEdges = _mosaicEdges.Where(ed => ed.IsVertical == clickedEdge.IsVertical).OrderBy(ed => clickedEdge.IsVertical ? ed.X : ed.Y).ToList();
                            double offset = 20.0;

                            if (globalEdges.Count >= 2)
                            {
                                int idx = globalEdges.IndexOf(clickedEdge);
                                if (idx >= 0 && idx < globalEdges.Count - 1)
                                    offset = clickedEdge.IsVertical ? globalEdges[idx + 1].X - globalEdges[idx].X : globalEdges[idx + 1].Y - globalEdges[idx].Y;
                                else if (idx > 0)
                                    offset = clickedEdge.IsVertical ? globalEdges[idx].X - globalEdges[idx - 1].X : globalEdges[idx].Y - globalEdges[idx - 1].Y;
                            }
                            if (offset <= 0) offset = 20.0;

                            double newX = clickedEdge.IsVertical ? clickedEdge.X + offset : clickedEdge.X;
                            double newY = clickedEdge.IsVertical ? clickedEdge.Y : clickedEdge.Y + offset;

                            _mosaicEdges.Add(new MosaicEdge(clickedEdge.IsVertical, newX, newY, clickedEdge.Length));
                            ImageCanvas.Invalidate();
                        };
                        flyout.Items.Add(duplicateItem);

                        var deleteItem = new MenuFlyoutItem { Text = "Delete Line" };
                        deleteItem.Click += (s, args) =>
                        {
                            _mosaicEdges.Remove(clickedEdge);
                            _undoStack.Push(() => _mosaicEdges.Add(clickedEdge));
                            ImageCanvas.Invalidate();
                        };
                        flyout.Items.Add(deleteItem);
                    }
                    else
                    {
                        if (_mosaicMarks.Count > 0)
                        {
                            var batchHItem = new MenuFlyoutItem { Text = "Convert All Marks to Short H-Lines" };
                            batchHItem.Click += (s, args) =>
                            {
                                var oldMarks = new List<MosaicMark>(_mosaicMarks);
                                var newEdges = new List<MosaicEdge>();

                                foreach (var m in _mosaicMarks)
                                {
                                    var newEdge = new MosaicEdge(false, m.X - 25, m.Y, 50);
                                    _mosaicEdges.Add(newEdge);
                                    newEdges.Add(newEdge);
                                }

                                _mosaicMarks.Clear();
                                _undoStack.Push(() => { foreach (var ed in newEdges) _mosaicEdges.Remove(ed); _mosaicMarks.AddRange(oldMarks); });
                                ImageCanvas.Invalidate();
                            };
                            flyout.Items.Add(batchHItem);

                            var batchVItem = new MenuFlyoutItem { Text = "Convert All Marks to Short V-Lines" };
                            batchVItem.Click += (s, args) =>
                            {
                                var oldMarks = new List<MosaicMark>(_mosaicMarks);
                                var newEdges = new List<MosaicEdge>();

                                foreach (var m in _mosaicMarks)
                                {
                                    var newEdge = new MosaicEdge(true, m.X, m.Y - 25, 50);
                                    _mosaicEdges.Add(newEdge);
                                    newEdges.Add(newEdge);
                                }

                                _mosaicMarks.Clear();
                                _undoStack.Push(() => { foreach (var ed in newEdges) _mosaicEdges.Remove(ed); _mosaicMarks.AddRange(oldMarks); });
                                ImageCanvas.Invalidate();
                            };
                            flyout.Items.Add(batchVItem);

                            var clearMarksItem = new MenuFlyoutItem { Text = "Clear All Marks" };
                            clearMarksItem.Click += (s, args) => { _mosaicMarks.Clear(); ImageCanvas.Invalidate(); };
                            flyout.Items.Add(clearMarksItem);

                            flyout.Items.Add(new MenuFlyoutSeparator());
                        }

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
                    if (hitMark != null)
                    {
                        _activeMark = hitMark;
                        _lastMousePos = pos;
                        OverlayCanvas.CapturePointer(e.Pointer);
                        ImageCanvas.Invalidate();
                        e.Handled = true;
                    }
                    else if (hitResult.edge != null)
                    {
                        _activeEdge = hitResult.edge;
                        _dragMode = hitResult.mode;
                        _lastMousePos = pos;
                        OverlayCanvas.CapturePointer(e.Pointer);
                        ImageCanvas.Invalidate();
                        e.Handled = true;
                    }
                    else
                    {
                        _activeEdge = null;
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
                _ = ZoomToFitCropBox();
            }
        }

        private void OverlayCanvas_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_isPanning)
            {
                var screenPos = e.GetCurrentPoint(null).Position;

                // Divide the raw screen movement by the zoom factor so panning matches mouse speed 1:1
                double zoom = ImageScrollViewer.ZoomFactor;
                if (zoom <= 0) zoom = 1.0; // Safety fallback

                double dx = (_lastPanScreenPos.X - screenPos.X) / zoom;
                double dy = (_lastPanScreenPos.Y - screenPos.Y) / zoom;

                // Accumulate the movement into our custom variables
                _panTargetX += dx;
                _panTargetY += dy;

                // Pass the custom variables, completely ignoring the delayed native offsets
                ImageScrollViewer.ChangeView(_panTargetX, _panTargetY, null, true);

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

            if (_isMosaicMode && _activeMark != null)
            {
                double dx = pos.X - _lastMousePos.X;
                double dy = pos.Y - _lastMousePos.Y;
                _activeMark.X += dx;
                _activeMark.Y += dy;
                _lastMousePos = pos;
                ImageCanvas.Invalidate();
                return;
            }

            if (_isMosaicMode && _activeEdge != null)
            {
                double dx = pos.X - _lastMousePos.X;
                double dy = pos.Y - _lastMousePos.Y;

                // Direct hardware read for Alt
                bool isAltPressed = NativeInterop.IsAltPressed();

                if (_dragMode == DragMode.Body)
                {
                    double moveX = dx;
                    double moveY = dy;

                    if (isAltPressed)
                    {
                        if (_activeEdge.IsVertical) moveY = 0;
                        else moveX = 0;
                    }

                    _activeEdge.Move(moveX, moveY);
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

            if (_isMosaicMode && _activeMark != null)
            {
                _activeMark = null;
                OverlayCanvas.ReleasePointerCapture(e.Pointer);
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
        }

        private async void RootGrid_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Escape && _isMosaicMode)
            {
                e.Handled = true;
                await ExitMosaicMode();
            }
            else if (e.Key == Windows.System.VirtualKey.Z && NativeInterop.IsCtrlPressed() && _isMosaicMode)
            {
                if (_undoStack.Count > 0)
                {
                    _undoStack.Pop().Invoke();
                    ImageCanvas.Invalidate();
                    e.Handled = true;
                }
            }
        }

        private void EnterMosaicMode()
        {
            if (_currentImage == null) return;

            int size = _cropSizes[CropSizeComboBox.SelectedIndex];

            ImageCanvas.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(_regionX, _regionY, size, size)
            };

            PrevButton.IsEnabled = false;
            NextButton.IsEnabled = false;
            LoadDirectoryButton.IsEnabled = false;
            ClearButton.IsEnabled = false;
            RegionComboBox.IsEnabled = false;
            NewRegionButton.IsEnabled = false;

            ImageCanvas.Invalidate();
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
            RegionComboBox.IsEnabled = true;
            NewRegionButton.IsEnabled = true;

            // Force the canvas to redraw and show the green crop square
            ImageCanvas.Invalidate();

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

        private int CountMosaicRectangles()
        {
            var hLines = _mosaicEdges.Where(e => !e.IsVertical).ToList();
            var vLines = _mosaicEdges.Where(e => e.IsVertical).ToList();
            int rectangleCount = 0;
            double tol = 2.0;

            foreach (var top in hLines)
            {
                foreach (var bottom in hLines)
                {
                    if (top.Y + tol >= bottom.Y) continue; // Top must be strictly above bottom

                    foreach (var left in vLines)
                    {
                        foreach (var right in vLines)
                        {
                            if (left.X + tol >= right.X) continue; // Left must be strictly left of right

                            // Check if the 4 segments physically intersect to form this specific box
                            bool topCovers = top.X - tol <= left.X && (top.X + top.Length + tol) >= right.X;
                            bool bottomCovers = bottom.X - tol <= left.X && (bottom.X + bottom.Length + tol) >= right.X;
                            bool leftCovers = left.Y - tol <= top.Y && (left.Y + left.Length + tol) >= bottom.Y;
                            bool rightCovers = right.Y - tol <= top.Y && (right.Y + right.Length + tol) >= bottom.Y;

                            if (topCovers && bottomCovers && leftCovers && rightCovers)
                            {
                                bool isAtomic = true; // Assume it's a valid empty cell until proven otherwise

                                // Check if any other horizontal line crosses inside the box
                                foreach (var h in hLines)
                                {
                                    if (h == top || h == bottom) continue;
                                    if (h.Y > top.Y + tol && h.Y < bottom.Y - tol)
                                    {
                                        if (h.X - tol < right.X && (h.X + h.Length + tol) > left.X)
                                        {
                                            isAtomic = false;
                                            break;
                                        }
                                    }
                                }

                                if (!isAtomic) continue;

                                // Check if any other vertical line crosses inside the box
                                foreach (var v in vLines)
                                {
                                    if (v == left || v == right) continue;
                                    if (v.X > left.X + tol && v.X < right.X - tol)
                                    {
                                        if (v.Y - tol < bottom.Y && (v.Y + v.Length + tol) > top.Y)
                                        {
                                            isAtomic = false;
                                            break;
                                        }
                                    }
                                }

                                if (isAtomic) rectangleCount++;
                            }
                        }
                    }
                }
            }
            return rectangleCount;
        }
    }

    public static class NativeInterop
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        public static bool IsShiftPressed() => (GetAsyncKeyState(0x10) & 0x8000) != 0;
        public static bool IsCtrlPressed() => (GetAsyncKeyState(0x11) & 0x8000) != 0;
        public static bool IsAltPressed() => (GetAsyncKeyState(0x12) & 0x8000) != 0;

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