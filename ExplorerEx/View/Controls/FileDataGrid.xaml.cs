﻿using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using ExplorerEx.Converter;
using ExplorerEx.Model;
using ExplorerEx.Utils;
using ExplorerEx.Win32;
using HandyControl.Controls;
using HandyControl.Tools;
using ScrollViewer = System.Windows.Controls.ScrollViewer;
using TabItem = HandyControl.Controls.TabItem;
using TextBox = HandyControl.Controls.TextBox;

namespace ExplorerEx.View.Controls;

/// <summary>
/// 要能够响应鼠标事件，处理点选、框选、拖放、重命名和双击
/// </summary>
public partial class FileDataGrid {
	public enum ViewTypes {
		/// <summary>
		/// 图标，表现为WarpPanel，每个小格上边是缩略图下边是文件名
		/// </summary>
		Icon,
		/// <summary>
		/// 列表，表现为WarpPanel，每个小格左边是缩略图右边是文件名
		/// </summary>
		List,
		/// <summary>
		/// 详细信息，表现为DataGrid，上面有Header，一列一列的
		/// </summary>
		Detail,
		/// <summary>
		/// 平铺，表现为WarpPanel，每个小格左边是缩略图右边从上到下依次是文件名、文件类型描述、文件大小
		/// </summary>
		Tile,
		/// <summary>
		/// 内容，表现为DataGrid，但Header不可见，左边是图标、文件名、大小，右边是详细信息
		/// </summary>
		Content
	}

	/// <summary>
	/// 选择详细信息中的显示列
	/// </summary>
	[Flags]
	public enum Lists {
		// Name必选，忽略

		/// <summary>
		/// 修改日期
		/// </summary>
		ModificationDate = 1,
		/// <summary>
		/// 类型
		/// </summary>
		Type = 2,
		/// <summary>
		/// 文件大小
		/// </summary>
		FileSize = 4,
		/// <summary>
		/// 创建日期
		/// </summary>
		CreationDate = 8,

		// 以下为驱动器的列
		/// <summary>
		/// 可用空间
		/// </summary>
		AvailableSpace = 16,
		/// <summary>
		/// 总大小
		/// </summary>
		TotalSpace = 32,
		/// <summary>
		/// 文件系统（NTFS、FAT）
		/// </summary>
		FileSystem = 64,
		/// <summary>
		/// 填充的百分比
		/// </summary>
		FillRatio = 128,
	}

	/// <summary>
	/// 当前路径的类型
	/// </summary>
	public enum PathTypes {
		/// <summary>
		/// 首页，“此电脑”
		/// </summary>
		Home,
		/// <summary>
		/// 普通，即浏览正常文件
		/// </summary>
		Normal,
		/// <summary>
		/// 搜索文件的结果
		/// </summary>
		Search,
		/// <summary>
		/// 网络驱动器
		/// </summary>
		NetworkDisk,
	}

	private ScrollViewer scrollViewer;
	private Grid contentGrid;
	private Border selectionRect;

	/// <summary>
	/// 如果不为null，说明正在Drag，可以修改Destination或者Effect
	/// </summary>
	public static DragFilesPreview DragFilesPreview { get; private set; }

	private static DragDropWindow dragDropWindow;
	private static FileViewBaseItem[] draggingItems;

	/// <summary>
	/// 自带的Items索引方法比较复杂，使用这个
	/// </summary>
	public new ObservableCollection<FileViewBaseItem> Items => (ObservableCollection<FileViewBaseItem>)ItemsSource;

	public delegate void FileDropEventHandler(object sender, FileDropEventArgs e);

	public static readonly RoutedEvent FileDropEvent = EventManager.RegisterRoutedEvent(
		"FileDrop", RoutingStrategy.Bubble, typeof(FileDropEventHandler), typeof(FileDataGrid));

	public event FileDropEventHandler FileDrop {
		add => AddHandler(FileDropEvent, value);
		remove => RemoveHandler(FileDropEvent, value);
	}

	public delegate void ItemClickEventHandler(object sender, ItemClickEventArgs e);

	public static readonly RoutedEvent ItemClickedEvent = EventManager.RegisterRoutedEvent(
		"ItemClicked", RoutingStrategy.Bubble, typeof(ItemClickEventHandler), typeof(FileDataGrid));

	public event ItemClickEventHandler ItemClicked {
		add => AddHandler(ItemClickedEvent, value);
		remove => RemoveHandler(ItemClickedEvent, value);
	}

	public static readonly RoutedEvent ItemDoubleClickedEvent = EventManager.RegisterRoutedEvent(
		"ItemDoubleClicked", RoutingStrategy.Bubble, typeof(ItemClickEventHandler), typeof(FileDataGrid));

	public event ItemClickEventHandler ItemDoubleClicked {
		add => AddHandler(ItemDoubleClickedEvent, value);
		remove => RemoveHandler(ItemDoubleClickedEvent, value);
	}

	public static readonly DependencyProperty ViewTypeProperty = DependencyProperty.Register(
		"ViewType", typeof(ViewTypes), typeof(FileDataGrid), new PropertyMetadata(ViewTypes.Tile, ViewType_OnChanged));

	private static void ViewType_OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) {
		if (e.NewValue != e.OldValue) {
			((FileDataGrid)d).UpdateColumns();
		}
	}

	public ViewTypes ViewType {
		get => (ViewTypes)GetValue(ViewTypeProperty);
		set => SetValue(ViewTypeProperty, value);
	}

	public static readonly DependencyProperty PathTypeProperty = DependencyProperty.Register(
		"PathType", typeof(PathTypes), typeof(FileDataGrid), new PropertyMetadata(PathTypes.Home));

	public PathTypes PathType {
		get => (PathTypes)GetValue(PathTypeProperty);
		set => SetValue(PathTypeProperty, value);
	}

	public static readonly DependencyProperty ItemSizeProperty = DependencyProperty.Register(
		"ItemSize", typeof(Size), typeof(FileDataGrid), new PropertyMetadata(default(Size)));

	/// <summary>
	/// 项目大小
	/// </summary>
	public Size ItemSize {
		get => (Size)GetValue(ItemSizeProperty);
		set => SetValue(ItemSizeProperty, value);
	}

	public static readonly DependencyProperty DetailListsProperty = DependencyProperty.Register(
		"DetailLists", typeof(Lists), typeof(FileDataGrid), new PropertyMetadata(default(Lists), DetailLists_OnChanged));

	private static void DetailLists_OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) {
		if (e.NewValue != e.OldValue) {
			((FileDataGrid)d).UpdateColumns();
		}
	}

	public Lists DetailLists {
		get => (Lists)GetValue(DetailListsProperty);
		set => SetValue(DetailListsProperty, value);
	}

	public static readonly DependencyProperty FullPathProperty = DependencyProperty.Register(
		"FullPath", typeof(string), typeof(FileDataGrid), new PropertyMetadata(default(string)));

	public string FullPath {
		get => (string)GetValue(FullPathProperty);
		set => SetValue(FullPathProperty, value);
	}
	
	/// <summary>
	/// 选择一个文件，参数为string文件名，不含路径
	/// </summary>
	public SimpleCommand SelectCommand { get; }

	/// <summary>
	/// 选择并重命名一个文件，参数为string文件名，不含路径
	/// </summary>
	public SimpleCommand StartRenameCommand { get; }

	private readonly FileDataGridColumnsConverter columnsConverter;

	public FileDataGrid() {
		InitializeComponent();
		EventManager.RegisterClassHandler(typeof(TextBox), GotFocusEvent, new RoutedEventHandler(OnRenameTextBoxGotFocus));
		SelectCommand = new SimpleCommand(Select);
		StartRenameCommand = new SimpleCommand(StartRename);
		columnsConverter = (FileDataGridColumnsConverter)FindResource("ColumnsConverter");
	}

	/// <summary>
	/// 根据<see cref="PathType"/>、<see cref="ViewType"/>和<see cref="DetailLists"/>选择列，同时更新<see cref="DataGrid.ColumnHeaderHeight"/>
	/// </summary>
	private void UpdateColumns() {
		if (contentGrid == null) {
			return;
		}
		columnsConverter.Convert(Columns, PathType, ViewType, DetailLists);
		if (ViewType == ViewTypes.Detail) {
			ColumnHeaderHeight = 20d;
			contentGrid.Margin = new Thickness(Padding.Left, 20 + Padding.Top, Padding.Right, Padding.Bottom);
		} else {
			ColumnHeaderHeight = 0d;
			contentGrid.Margin = new Thickness(Padding.Left, Padding.Top, Padding.Right, Padding.Bottom);
		}
	}

	public void Select(object fileName) {
		var item = Items.FirstOrDefault(item => item.Name == (string)fileName);
		if (item != null) {
			ScrollIntoView(item);
			item.IsSelected = true;
		}
	}

	public void StartRename(object fileName) {
		var item = Items.FirstOrDefault(item => item.Name == (string)fileName);
		if (item != null) {
			ScrollIntoView(item);
			item.IsSelected = true;
			item.BeginRename();
		}
	}

	private void OnRenameTextBoxGotFocus(object sender, RoutedEventArgs e) {
		var textBox = (TextBox)sender;
		if (textBox.DataContext is FileViewBaseItem item) {
			renamingItem = item;
			var lastIndexOfDot = textBox.Text.LastIndexOf('.');
			if (lastIndexOfDot == -1) {
				textBox.SelectAll();
			} else {
				textBox.Select(0, lastIndexOfDot);
			}
			e.Handled = true;
		}
	}

	public override void OnApplyTemplate() {
		base.OnApplyTemplate();
		scrollViewer = (ScrollViewer)GetTemplateChild("DG_ScrollViewer");
		contentGrid = (Grid)GetTemplateChild("ContentGrid");
		selectionRect = (Border)GetTemplateChild("SelectionRect");
		UpdateColumns();
	}

	/// <summary>
	/// 是否鼠标点击了，不加这个可能会从外部拖进来依旧是框选状态
	/// </summary>
	private bool isMouseDown;

	private bool isPreparedForRenaming;
	/// <summary>
	/// 鼠标按下如果在Row上，就是对应的项；如果不在，就是-1。每次鼠标抬起都重置为-1
	/// </summary>
	private int mouseDownRowIndex;
	/// <summary>
	/// <see cref="mouseDownRowIndex"/>重置为-1之前的值，主要用于shift多选
	/// </summary>
	private int lastMouseDownRowIndex;

	private Point startDragPosition;

	/// <summary>
	/// 是否正在框选
	/// </summary>
	private bool isRectSelecting;
	/// <summary>
	/// 是否正在拖放
	/// </summary>
	private bool isDragDropping;

	private Point startSelectionPoint;
	private DispatcherTimer timer;

	private FileViewBaseItem renamingItem;
	private CancellationTokenSource renameCts;

	protected override void OnItemsChanged(NotifyCollectionChangedEventArgs e) {
		if (e.OldStartingIndex == lastMouseDownRowIndex) {
			lastMouseDownRowIndex = -1;
		}
		if (e.OldStartingIndex == mouseDownRowIndex) {
			mouseDownRowIndex = -1;
		}
		base.OnItemsChanged(e);
	}

	/// <summary>
	/// 用于处理双击事件
	/// </summary>
	private FileViewBaseItem lastClickItem;
	private DateTimeOffset lastClickTime;
	private bool isDoubleClicked;

	/// <summary>
	/// 认真观察了自带文件管理器的交互方式。
	/// 当鼠标左键或右键按下时，分以下几种情况：
	///   如果当前正在重命名，那就判断点击的位置是不是重命名的TextBox，如果是就不处理，否则就立即应用重命名并退出重命名状态
	/// 
	///   如果点击在了项目上
	///	    如果之前没有选中项，那就立即选中该项（“立即”指不等鼠标键抬起）
	///     如果之前选中了其他项目（单选或者多选），那就立即清除其他项的选择，并选中该项
	///     如果之前有且只有当前项目选中，那么就什么也不做，但是要在鼠标键左键（右键不计时）松开之后开始计时，计时结束前若没有其他操作就开始该项的重命名
	///     如果之前选中了多个项目，且该项也是选择状态，就什么也不做。此时如果松开按键，那就取消选择其他项，只选中当前项
	/// 
	///   没有点击在项目上，那就清空选择，同时记录坐标，为框选做准备
	/// </summary>
	/// <param name="e"></param>
	protected override void OnPreviewMouseDown(MouseButtonEventArgs e) {
		isDoubleClicked = false;
		if (e.OriginalSource.FindParent<ScrollBar, FileDataGrid>() != null) {
			base.OnPreviewMouseDown(e);
			return;
		}

		if (e.ChangedButton is MouseButton.Left or MouseButton.Right) {
			isMouseDown = true;

			if (renameCts != null) {
				renameCts.Cancel();
				renameCts = null;
			}
			if (renamingItem is { EditingName: not null }) {
				if (e.OriginalSource.FindParent<TextBox, FileDataGrid>() == null) {
					renamingItem.StopRename();
				}
			}

			if (ContainerFromElement(this, (DependencyObject)e.OriginalSource) is DataGridRow row) {
				var item = (FileViewBaseItem)row.Item;
				// 双击
				if (item == lastClickItem && DateTimeOffset.Now <= lastClickTime.AddMilliseconds(Win32Interop.GetDoubleClickTime())) {
					isDoubleClicked = true;
					renameCts?.Cancel();
					renameCts = null;
					UnselectAll();
					RaiseEvent(new ItemClickEventArgs(ItemDoubleClickedEvent, item));
				} else {
					mouseDownRowIndex = Items.IndexOf(item);
					if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl)) {
						lastMouseDownRowIndex = mouseDownRowIndex;
						row.IsSelected = !row.IsSelected;
					} else if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)) {
						if (lastMouseDownRowIndex == -1) {
							lastMouseDownRowIndex = mouseDownRowIndex;
							row.IsSelected = true;
						} else {
							if (mouseDownRowIndex != lastMouseDownRowIndex) {
								int startIndex, endIndex;
								if (mouseDownRowIndex < lastMouseDownRowIndex) {
									startIndex = mouseDownRowIndex;
									endIndex = lastMouseDownRowIndex;
								} else {
									startIndex = lastMouseDownRowIndex;
									endIndex = mouseDownRowIndex;
								}
								UnselectAll();
								for (var i = startIndex; i <= endIndex; i++) {
									Items[i].IsSelected = true;
								}
							}
						}
					} else {
						lastMouseDownRowIndex = mouseDownRowIndex;
						var selectedItems = SelectedItems;
						if (selectedItems.Count == 0) {
							row.IsSelected = true;
						} else if (!row.IsSelected) {
							UnselectAll();
							row.IsSelected = true;
						} else if (selectedItems.Count == 1 && e.ChangedButton == MouseButton.Left) {
							isPreparedForRenaming = true;
						}
					}
				}
				lastClickItem = item;
				lastClickTime = DateTimeOffset.Now;
			} else {
				mouseDownRowIndex = -1;
				startDragPosition = e.GetPosition(contentGrid);
				var x = Math.Min(Math.Max(startDragPosition.X, 0), contentGrid.ActualWidth);
				var y = Math.Min(Math.Max(startDragPosition.Y, 0), contentGrid.ActualHeight);
				startSelectionPoint = new Point(x + scrollViewer.HorizontalOffset, y + scrollViewer.VerticalOffset);
			}
		}
		e.Handled = true;
	}

	/// <summary>
	/// 框选或者拖放时，自动滚动的速度
	/// </summary>
	private Vector scrollSpeed;

	/// <summary>
	/// 鼠标移动时，分为以下几种情况
	///    如果鼠标点击在项目上，那就进行拖放，如果不在项目上，那就进行框选
	/// </summary>
	/// <param name="e"></param>
	protected override void OnPreviewMouseMove(MouseEventArgs e) {
		if (e.OriginalSource.FindParent<ScrollBar, FileDataGrid>() != null || isDoubleClicked) {
			base.OnPreviewMouseMove(e);
			return;
		}
		// 只有isMouseDown（即OnPreviewMouseDown触发过）为true，这个才有用
		if (isMouseDown && !isDragDropping && e.LeftButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed) {
			var point = e.GetPosition(contentGrid);
			if (Math.Abs(point.X - startDragPosition.X) > SystemParameters.MinimumHorizontalDragDistance ||
				Math.Abs(point.Y - startDragPosition.Y) > SystemParameters.MinimumVerticalDragDistance) {
				if (mouseDownRowIndex != -1) {
					draggingItems = SelectedItems.Cast<FileViewBaseItem>().ToArray();
					var data = new DataObject(DataFormats.FileDrop, draggingItems.Select(item => item.FullPath).ToArray(), true);
					var allowedEffects = draggingItems.Any(item => item is DiskDriveItem) ? DragDropEffects.Link : DragDropEffects.Copy | DragDropEffects.Link | DragDropEffects.Move;
					DragFilesPreview = new DragFilesPreview(draggingItems.Select(item => item.Icon).ToArray());
					dragDropWindow = new DragDropWindow(DragFilesPreview, new Point(50, 100), 0.8, false);
					isDragDropping = true;
					DragDrop.DoDragDrop(this, data, allowedEffects);
					draggingItems = null;
					isDragDropping = false;
					if (dragDropWindow != null) {
						dragDropWindow.Close();
						dragDropWindow = null;
					}
					DragFilesPreview = null;
				} else {
					if (!isRectSelecting) {
						UnselectAll();
						if (lastTIndex <= lastBIndex) {
							var items = Items;
							for (var i = lastTIndex; i <= lastBIndex; i++) {
								items[i].IsSelected = false;
							}
							lastTIndex = Items.Count;
							lastBIndex = -1;
						}
						selectionRect.Visibility = Visibility.Visible;
						Mouse.Capture(this);
						scrollSpeed = new Vector();
						timer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(20), DispatcherPriority.Input, RectSelectScroll, Dispatcher);
						timer.Start();
						isRectSelecting = true;
					}
					UpdateRectSelection();

					if (point.X < 0) {
						scrollSpeed.X = point.X / 10d;
					} else if (point.Y > ActualWidth) {
						scrollSpeed.X = (point.X - ActualWidth) / 10d;
					} else {
						scrollSpeed.X = 0;
					}
					if (point.Y < 0) {
						scrollSpeed.Y = point.Y / 10d;
					} else if (point.Y > ActualHeight) {
						scrollSpeed.Y = (point.Y - ActualHeight) / 10d;
					} else {
						scrollSpeed.Y = 0;
					}
				}
			}
		}
		e.Handled = true;
	}

	private int lastLIndex, lastRIndex, lastTIndex, lastBIndex;

	/// <summary>
	/// 计算框选的元素
	/// </summary>
	private void UpdateRectSelection() {
		var point = Mouse.GetPosition(contentGrid);
		var x = Math.Min(Math.Max(point.X, 0), contentGrid.ActualWidth) + scrollViewer.HorizontalOffset;
		var y = Math.Min(Math.Max(point.Y, 0), contentGrid.ActualHeight) + scrollViewer.VerticalOffset;
		double l, t, w, h;
		if (x < startSelectionPoint.X) {
			l = x;
			w = startSelectionPoint.X - x;
		} else {
			l = startSelectionPoint.X;
			w = x - startSelectionPoint.X;
		}
		if (y < startSelectionPoint.Y) {
			t = y;
			h = startSelectionPoint.Y - y;
		} else {
			t = startSelectionPoint.Y;
			h = y - startSelectionPoint.Y;
		}
		selectionRect.Margin = new Thickness(l - scrollViewer.HorizontalOffset, t - scrollViewer.VerticalOffset, 0, 0);
		selectionRect.Width = w;
		selectionRect.Height = h;

		if (Items.Count > 0) {
			var items = Items;
			var itemWidth = ItemSize.Width;
			var itemHeight = ItemSize.Height;
			var dY = itemHeight + 4;  // 上下两项的y值之差，4是两项之间的间距，是固定的值
			if (itemWidth > 0) {
				return;  // 暂不支持Grid框选
				var xCount = (int)(contentGrid.ActualWidth / itemWidth);  // 横向能容纳多少个元素
				var yCount = (int)MathF.Ceiling((float)items.Count / xCount);  // 纵向有多少行
				var dX = contentGrid.ActualWidth / xCount;
				var lIndex = Math.Max((int)((l + 4) / dX), 0);
				var rIndex = Math.Min((int)((l + w) / dX), xCount - 1);
				var tIndex = Math.Max((int)((t + 4) / dY), 0);
				var bIndex = Math.Min((int)((h + t) / dY), yCount - 1);
				var selectedCount = 0;
				if (lIndex != lastLIndex && lIndex < xCount) {
					for (var yy = lastTIndex; yy <= lastBIndex; yy++) {
						var i = yy * xCount;
						for (var xx = lastLIndex; xx <= lIndex; xx++) {
							var j = i + xx;
							if (j < items.Count) {
								items[j].IsSelected = false;
							}
						}
					}
					for (var yy = tIndex; yy <= bIndex; yy++) {
						var i = yy * xCount;
						for (var xx = lIndex; xx <= lastLIndex && yy <= rIndex; xx++) {
							var j = i + xx;
							if (j < items.Count) {
								items[j].IsSelected = true;
								selectedCount++;
							}
						}
					}
					lastLIndex = lIndex;
				}
				if (rIndex != lastRIndex && rIndex >= 0) {
					for (var yy = lastTIndex; yy <= lastBIndex; yy++) {
						var i = yy * xCount;
						for (var xx = rIndex + 1; yy <= lastRIndex; xx++) {
							var j = i + xx;
							if (j < items.Count) {
								items[j].IsSelected = false;
							}
						}
					}
					for (var yy = tIndex; yy <= bIndex; yy++) {
						var i = yy * xCount;
						for (var xx = Math.Max(lastRIndex + 1, lIndex); xx <= rIndex; xx++) {
							var j = i + xx;
							if (j < items.Count) {
								items[j].IsSelected = true;
								selectedCount++;
							}
						}
					}
					lastRIndex = rIndex;
				}
				if (tIndex != lastTIndex && tIndex < yCount) {
					for (var yy = lastTIndex; yy <= tIndex; yy++) {
						var i = yy * xCount;
						for (var xx = lastLIndex; xx <= lastRIndex; xx++) {
							var j = i + xx;
							if (j < items.Count) {
								items[j].IsSelected = false;
							}
						}
					}
					for (var yy = tIndex; yy <= lastTIndex && yy <= bIndex; yy++) {
						var i = yy * xCount;
						for (var xx = lIndex; xx <= rIndex; xx++) {
							var j = i + xx;
							if (j < items.Count) {
								items[j].IsSelected = true;
								selectedCount++;
							}
						}
					}
					lastTIndex = tIndex;
				}
				if (bIndex != lastBIndex && bIndex >= 0) {
					for (var yy = bIndex + 1; yy <= lastBIndex; yy++) {
						var i = yy * xCount;
						for (var xx = lastLIndex; xx <= lastRIndex; xx++) {
							var j = i + xx;
							if (j < items.Count) {
								items[j].IsSelected = false;
							}
						}
					}
					for (var yy = Math.Max(lastBIndex + 1, tIndex); yy <= bIndex; yy++) {
						var i = yy * xCount;
						for (var xx = lIndex; xx <= rIndex; xx++) {
							var j = i + xx;
							if (j < items.Count) {
								items[j].IsSelected = true;
								selectedCount++;
							}
						}
					}
					lastBIndex = bIndex;
				}
				if (selectedCount == 0 && lastLIndex <= lastRIndex && lastTIndex <= lastBIndex) {
					for (var yy = lastTIndex; yy <= lastBIndex; yy++) {
						var i = yy * xCount;
						for (var xx = lastLIndex; xx <= lastRIndex; xx++) {
							var j = i + xx;
							if (j < items.Count) {
								items[j].IsSelected = false;
							}
						}
					}
					lastLIndex = xCount;
					lastRIndex = -1;
					lastTIndex = yCount;
					lastBIndex = -1;
				}
			} else {  // 只计算纵向
				var firstIndex = (int)(scrollViewer.VerticalOffset / dY);  // 视图中第一个元素的index，因为使用了虚拟化容器，所以不能找Items[0]，它可能不存在
				var row0 = (DataGridRow)ItemContainerGenerator.ContainerFromIndex(firstIndex);
				// l = 0, t = 0时，正好是第一个元素左上的坐标
				if (l < row0.DesiredSize.Width) {  // 框的左边界在列内。每列Width都是一样的
					var tIndex = Math.Max((int)((t + 4) / dY), 0);
					var bIndex = Math.Min((int)((h + t) / dY), items.Count - 1);
					if (tIndex != lastTIndex && tIndex < items.Count) {
						for (var i = lastTIndex; i < tIndex; i++) {
							items[i].IsSelected = false;
						}
						for (var i = tIndex; i <= lastTIndex && i <= bIndex; i++) {
							items[i].IsSelected = true;
						}
						lastTIndex = tIndex;
					}
					if (bIndex != lastBIndex && bIndex >= 0) {
						for (var i = bIndex + 1; i <= lastBIndex; i++) {
							items[i].IsSelected = false;
						}
						for (var i = Math.Max(lastBIndex + 1, tIndex); i <= bIndex; i++) {
							items[i].IsSelected = true;
						}
						lastBIndex = bIndex;
					}
				} else if (lastTIndex <= lastBIndex) {
					for (var i = lastTIndex; i <= lastBIndex; i++) {
						items[i].IsSelected = false;
					}
					lastTIndex = items.Count;
					lastBIndex = -1;
				}
			}
		}
	}

	protected override void OnPreviewMouseUp(MouseButtonEventArgs e) {
		if (e.OriginalSource.FindParent<ScrollBar, FileDataGrid>() != null || isDoubleClicked) {
			base.OnPreviewMouseUp(e);
			return;
		}
		// 只有isMouseDown（即OnPreviewMouseDown触发过）为true，这个才有用
		if (isMouseDown && e.ChangedButton is MouseButton.Left or MouseButton.Right) {
			isMouseDown = false;
			if (isRectSelecting) {
				isRectSelecting = false;
				selectionRect.Visibility = Visibility.Collapsed;
				Mouse.Capture(null);
				timer?.Stop();
			} else if (isPreparedForRenaming) {
				isPreparedForRenaming = false;
				if (mouseDownRowIndex >= 0 && mouseDownRowIndex < Items.Count) {  // 防止集合改变过
					var item = Items[mouseDownRowIndex];
					if (renameCts == null) {
						var cts = renameCts = new CancellationTokenSource();
						Task.Run(() => {
							Thread.Sleep((int)(Win32Interop.GetDoubleClickTime() * 1.5f));  // 要比双击的时间长一些
							if (!cts.IsCancellationRequested) {
								item.BeginRename();
							}
						}, renameCts.Token);
					} else {
						renameCts = null;
					}
				}
			} else {
				var isClickOnItem = mouseDownRowIndex >= 0 && mouseDownRowIndex < Items.Count;
				var isCtrlOrShiftPressed = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl) ||
										   Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
				switch (e.ChangedButton) {
				case MouseButton.Left:
					if (isClickOnItem) {
						var item = Items[mouseDownRowIndex];
						if (!isCtrlOrShiftPressed && SelectedItems.Count > 1) {
							UnselectAll();
						}
						item.IsSelected = true;
						RaiseEvent(new ItemClickEventArgs(ItemClickedEvent, item));
					} else if (!isCtrlOrShiftPressed) {
						UnselectAll();
					}
					break;
				case MouseButton.Right:
					if (isClickOnItem) {
						var item = Items[mouseDownRowIndex];
						var menu = ((DataGridRow)ContainerFromElement(this, (DependencyObject)e.OriginalSource))!.ContextMenu!;
						menu.DataContext = item;
						menu.IsOpen = true;
					} else {
						// ContextMenu!.IsOpen = true;
					}
					break;
				}
			}
		}
		mouseDownRowIndex = -1;
		e.Handled = true;
	}

	/// <summary>
	/// 双击的时候，这个方法会接管<see cref="OnPreviewMouseUp"/>
	/// </summary>
	/// <param name="e"></param>
	protected override void OnPreviewMouseDoubleClick(MouseButtonEventArgs e) {
		if (mouseDownRowIndex >= 0 && mouseDownRowIndex < Items.Count && e.ChangedButton == MouseButton.Left) {
			renameCts?.Cancel();
			renameCts = null;
			RaiseEvent(new ItemClickEventArgs(ItemDoubleClickedEvent, Items[mouseDownRowIndex]));
		}
		e.Handled = true;
	}

	private void RectSelectScroll(object sender, EventArgs e) {
		scrollViewer.ScrollToHorizontalOffset(scrollViewer.HorizontalOffset + scrollSpeed.X);
		scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset + scrollSpeed.Y);
		UpdateRectSelection();
	}

	protected override void OnDragEnter(DragEventArgs e) {
		isDragDropping = true;
		if (PathType == PathTypes.Home && TabItem.DraggingTab == null) {
			e.Effects = DragDropEffects.None;
			e.Handled = true;
			return;
		}
		if (DragFilesPreview != null) {
			DragFilesPreview.Destination = FullPath;
		}
	}

	protected override void OnDragLeave(DragEventArgs e) {
		isDragDropping = false;
		if (DragFilesPreview != null) {
			DragFilesPreview.Destination = null;
		}
	}

	protected override void OnDragOver(DragEventArgs e) {
		isDragDropping = true;
		if (PathType == PathTypes.Home && TabItem.DraggingTab == null) {
			e.Effects = DragDropEffects.None;
			e.Handled = true;
			return;
		}
	}

	protected override void OnDrop(DragEventArgs e) {
		string path;
		// 拖动文件到了项目上
		if (ContainerFromElement(this, (DependencyObject)e.OriginalSource) is DataGridRow row) {
			var item = (FileViewBaseItem)row.Item;
			path = item.FullPath;
		} else {
			path = null;
		}
		RaiseEvent(new FileDropEventArgs(FileDropEvent, e, path));
	}

	protected override void OnPreviewGiveFeedback(GiveFeedbackEventArgs e) {
		if (e.Effects == DragDropEffects.None) {
			Mouse.SetCursor(Cursors.No);
			e.UseDefaultCursors = false;
			DragFilesPreview.Destination = null;
			e.Handled = true;
		} else if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)) {
			if (e.Effects.HasFlag(DragDropEffects.Move)) {
				DragFilesPreview.DragDropEffect = DragDropEffects.Move;
			}
		} else if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl)) {
			if (e.Effects.HasFlag(DragDropEffects.Copy)) {
				DragFilesPreview.DragDropEffect = DragDropEffects.Copy;
			}
		} else if (Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt)) {
			if (e.Effects.HasFlag(DragDropEffects.Link)) {
				DragFilesPreview.DragDropEffect = DragDropEffects.Link;
			}
		} else {
			DragFilesPreview.DragDropEffect = e.Effects.GetFirstEffect();
		}
		dragDropWindow.MoveWithCursor();
	}

	protected override void OnLostFocus(RoutedEventArgs e) {
		renamingItem?.StopRename();
		base.OnLostFocus(e);
	}

	public class ItemClickEventArgs : RoutedEventArgs {
		public FileViewBaseItem Item { get; }

		public ItemClickEventArgs(RoutedEvent e, FileViewBaseItem item) {
			RoutedEvent = e;
			Item = item;
		}
	}
}

public class FileDropEventArgs : RoutedEventArgs {
	public DragEventArgs DragEventArgs { get; }
	public DataObjectContent Content { get; }
	/// <summary>
	/// 拖动到的Path，可能是文件夹或者文件，为null表示当前路径
	/// </summary>
	public string Path { get; }

	public FileDropEventArgs(RoutedEvent e, DragEventArgs args, string path) {
		RoutedEvent = e;
		DragEventArgs = args;
		Content = new DataObjectContent(args.Data);
		Path = path;
	}
}