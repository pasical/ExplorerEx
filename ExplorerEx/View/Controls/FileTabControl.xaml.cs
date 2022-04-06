﻿using System;
using ExplorerEx.Utils;
using ExplorerEx.ViewModel;
using HandyControl.Data;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Windows.Input;
using ExplorerEx.Model;
using ExplorerEx.Win32;
using HandyControl.Controls;
using ConfigHelper = ExplorerEx.Utils.ConfigHelper;
using MessageBox = HandyControl.Controls.MessageBox;
using System.IO;
using ExplorerEx.Command;

namespace ExplorerEx.View.Controls;

public partial class FileTabControl {
	/// <summary>
	/// 获取当前被聚焦的TabControl
	/// </summary>
	public static FileTabControl FocusedTabControl { get; private set; }
	/// <summary>
	/// 当前鼠标正在其上的FileTabControl
	/// </summary>
	public static FileTabControl MouseOverTabControl { get; private set; }
	/// <summary>
	/// 所有的TabControl，一个分屏一个
	/// </summary>
	private static readonly List<FileTabControl> TabControls = new();

	/// <summary>
	/// 标签页
	/// </summary>
	public ObservableCollection<FileGridViewModel> TabItems { get; } = new();

	public new static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(
		"SelectedIndex", typeof(int), typeof(FileTabControl), new PropertyMetadata(default(int)));

	public new int SelectedIndex {
		get => (int)GetValue(SelectedIndexProperty);
		set {
			if (TabItems.Count == 0) {
				return;
			}
			if (value < 0) {
				value = 0;
			} else if (value >= TabItems.Count) {
				value = TabItems.Count - 1;
			}
			SetValue(SelectedIndexProperty, value);
		}
	}

	public FileGridViewModel SelectedTab {
		get {
			if (TabItems.Count == 0) {
				return null;
			}
			var index = SelectedIndex;
			if (index < 0 || index > TabItems.Count) {
				return TabItems[0];
			}
			return TabItems[index];
		}
	}

	public static readonly DependencyProperty IsFileUtilsVisibleProperty = DependencyProperty.Register(
		"IsFileUtilsVisible", typeof(bool), typeof(FileTabControl), new PropertyMetadata(default(bool)));

	public bool IsFileUtilsVisible {
		get => (bool)GetValue(IsFileUtilsVisibleProperty);
		set => SetValue(IsFileUtilsVisibleProperty, value);
	}

	public SimpleCommand TabClosingCommand { get; }
	public SimpleCommand TabMovedCommand { get; }
	public SimpleCommand TabCommand { get; }
	public SimpleCommand CreateNewTabCommand { get; }
	/// <summary>
	/// DragEnter、Over是一样的
	/// </summary>
	public SimpleCommand DragCommand { get; }
	public SimpleCommand DropCommand { get; }

	public MainWindow MainWindow { get; }

	public SplitGrid OwnerSplitGrid { get; set; }

	public FileTabControl(MainWindow mainWindow, SplitGrid ownerSplitGrid, FileGridViewModel grid) {
		MainWindow = mainWindow;
		OwnerSplitGrid = ownerSplitGrid;
		DataContext = this;
		TabClosingCommand = new SimpleCommand(OnTabClosing);
		TabMovedCommand = new SimpleCommand(OnTabMoved);
		TabCommand = new SimpleCommand(OnTabCommand);
		CreateNewTabCommand = new SimpleCommand(OnCreateNewTab);
		DragCommand = new SimpleCommand(OnDrag);
		DropCommand = new SimpleCommand(OnDrop);
		TabControls.Add(this);
		FocusedTabControl ??= this;
		MouseOverTabControl ??= this;

		InitializeComponent();
		if (grid != null) {
			TabItems.Add(grid);
		}
	}

	public async Task StartUpLoad(params string[] startupPaths) {
		if (startupPaths == null || startupPaths.Length == 0) {
			await OpenPathInNewTabAsync(null);
		} else {
			foreach (var path in startupPaths) {
				await OpenPathInNewTabAsync(path);
			}
		}
		if (TabItems.Count == 0) {
			await OpenPathInNewTabAsync(null);
		}
	}

	/// <summary>
	/// 关闭整个TabControl并释放资源
	/// </summary>
	public void Close() {
		foreach (var tab in TabItems) {
			tab.Dispose();
		}
		TabItems.Clear();
		TabControls.Remove(this);
		UpdateFocusedTabControl();
	}

	/// <summary>
	/// 关闭标签页
	/// </summary>
	/// <param name="tab"></param>
	public void CloseTab(FileGridViewModel tab) {
		var index = TabItems.IndexOf(tab);
		if (index == -1) {
			return;
		}
		var e = new CancelRoutedEventArgs(null, null);
		HandleTabClosing(tab, e);
		if (!e.Cancel) {
			TabItems.RemoveAt(index);
		}
	}

	/// <summary>
	/// 将标签页移动到新窗口，要求自己至少有两个以上的tab
	/// </summary>
	/// <param name="tab"></param>
	public void MoveTabToNewWindow(FileGridViewModel tab) {
		if (TabItems.Count == 1) {
			return;
		}
		var index = TabItems.IndexOf(tab);
		if (index == -1) {
			return;
		}
		var newWindow = new MainWindow(null, false);
		TabItems.RemoveAt(index);
		newWindow.SplitGrid.FileTabControl.TabItems.Add(tab);
		newWindow.Show();
	}

	public async Task OpenPathInNewTabAsync(string path, string selectedPath = null) {
		var newTabIndex = Math.Max(Math.Min(SelectedIndex + 1, TabItems.Count), 0);
		var item = new FileGridViewModel(this);
		TabItems.Insert(newTabIndex, item);
		SelectedIndex = newTabIndex;
		if (!await SelectedTab.LoadDirectoryAsync(path, true, selectedPath)) {
			if (TabItems.Count > 1) {
				TabItems.Remove(item);
			}
		}
	}

	protected override void OnPreviewMouseUp(MouseButtonEventArgs e) {
		switch (e.ChangedButton) {
		case MouseButton.XButton1:  // 鼠标侧键返回
			SelectedTab.GoBackAsync();
			break;
		case MouseButton.XButton2:
			SelectedTab.GoForwardAsync();
			break;
		}
		base.OnPreviewMouseUp(e);
	}

	protected override void OnGotFocus(RoutedEventArgs e) {
		FocusedTabControl = this;
		base.OnGotFocus(e);
	}

	/// <summary>
	/// 当前TabControl被关闭时，寻找下一个聚焦的TabControl
	/// </summary>
	private static void UpdateFocusedTabControl() {
		if (TabControls.Count == 0) {
			FocusedTabControl = null;
			return;
		}
		var focused = TabControls.FirstOrDefault(tc => tc.IsFocused);
		if (focused != null) {
			FocusedTabControl = focused;
		} else {
			focused = TabControls.FirstOrDefault(tc => tc.MainWindow.IsFocused);
			FocusedTabControl = focused ?? TabControls[0];
		}
	}

	private async void OnTabCommand(object args) {
		var e = (TabItemCommandArgs)args;
		var tabItem = e.TabItem;
		var tab = (FileGridViewModel)tabItem.DataContext;
		switch (e.CommandParameter) {
		case "Duplicate":
			await OpenPathInNewTabAsync((string)tabItem.FullPath);
			break;
		case "Move2NewWindow":
			MoveTabToNewWindow(tab);
			break;
		case "SplitLeft":
			if (OwnerSplitGrid.Split((FileGridViewModel)tabItem.DataContext, SplitOrientation.Left)) {
				TabItems.Remove(tab);
			}
			break;
		case "SplitRight":
			if (OwnerSplitGrid.Split((FileGridViewModel)tabItem.DataContext, SplitOrientation.Right)) {
				TabItems.Remove(tab);
			}
			break;
		case "SplitBottom":
			if (OwnerSplitGrid.Split((FileGridViewModel)tabItem.DataContext, SplitOrientation.Bottom)) {
				TabItems.Remove(tab);
			}
			break;
		}
	}

	/// <summary>
	/// 当一个Tab被移动到别的TabControl上时触发
	/// </summary>
	/// <param name="args"></param>
	private void OnTabMoved(object args) {
		if (TabItems.Count == 0) {  // 如果移走的是最后一个，那就要关闭当前的了
			TabControls.Remove(this);
			if (OwnerSplitGrid.AnyOtherTabs) {
				OwnerSplitGrid.CancelSplit();
			} else {  // 说明就剩这一个Tab了
				MainWindow.Close();
			}
			UpdateFocusedTabControl();
			MouseOverTabControl = FocusedTabControl;
		}
		// Trace.WriteLine(string.Join("\n", MainWindow.MainWindows.SelectMany(mw => mw.splitGrid).SelectMany(f => f.TabItems).Select(i => i.FullPath)));
	}

	private void OnTabClosing(object args) {
		var e = (CancelRoutedEventArgs)args;
		HandleTabClosing((FileGridViewModel)e.OriginalSource, e);
	}

	private async void HandleTabClosing(FileGridViewModel tab, CancelRoutedEventArgs e) {
		if (TabItems.Count <= 1) {
			if (OwnerSplitGrid.AnyOtherTabs) {
				tab.Dispose();
				TabControls.Remove(this);
				OwnerSplitGrid.CancelSplit();
				MouseOverTabControl = null;
				UpdateFocusedTabControl();
			} else {  // 说明就剩这一个Tab了
				if (e != null) {
					e.Cancel = true;
					e.Handled = true;
				}
				switch (ConfigHelper.LoadInt("LastTabClosed")) {
				case 1:
					tab.Dispose();
					MouseOverTabControl = null;
					MainWindow.Close();
					break;
				case 2:
					await SelectedTab.LoadDirectoryAsync(null);
					break;
				default:
					var msi = new MessageBoxInfo {
						Button = MessageBoxButton.OKCancel,
						OkButtonText = "CloseWindow".L(),
						CancelButtonText = "BackToHome".L(),
						Message = "#YouClosedTheLastTab".L(),
						CheckBoxText = "RememberMyChoiceAndDontAskAgain".L(),
						IsChecked = false,
						Image = MessageBoxImage.Question
					};
					var result = MessageBox.Show(msi);
					if (msi.IsChecked) {
						ConfigHelper.Save("LastTabClosed", result == MessageBoxResult.OK ? 1 : 2);
					}
					if (result == MessageBoxResult.OK) {
						tab.Dispose();
						MouseOverTabControl = null;
						MainWindow.Close();
					} else {
						await SelectedTab.LoadDirectoryAsync(null);
					}
					break;
				}
			}
		} else {
			tab.Dispose();
		}
	}

	private async void OnCreateNewTab(object args) {
		await OpenPathInNewTabAsync(null);
	}

	protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo) {
		base.OnRenderSizeChanged(sizeInfo);
		if (sizeInfo.WidthChanged) {
			IsFileUtilsVisible = sizeInfo.NewSize.Width > 640d;
		}
	}

	private static bool CanDragDrop(DragEventArgs e, FileGridViewModel vm) {
		if (vm.PathType == PathType.Home) {
			e.Effects = DragDropEffects.None;
			return false;
		}
		if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } fileList) {
			if (Path.GetDirectoryName(fileList[0]) == vm.FullPath) {  // 相同文件夹禁止移动
				e.Effects = DragDropEffects.None;
				return false;
			}
		}
		return true;
	}

	private static void OnDrag(object args) {
		if (args is TabItemDragEventArgs ti) {
			var tab = (FileGridViewModel)ti.TabItem.DataContext;
			if (!CanDragDrop(ti.DragEventArgs, tab)) {
				return;
			}
			if (FileListView.DragFilesPreview != null) {
				FileListView.DragFilesPreview.Destination = tab.FullPath;
			}
		} else {
			var tb = (TabBorderDragEventArgs)args;
			if (tb.DragEventArgs.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } fileList) {
				var folderList = fileList.Select(Directory.Exists).ToImmutableList();
				if (folderList.Count > 0) {
					if (FileListView.DragFilesPreview != null) {
						FileListView.DragFilesPreview.CustomOperation = "OpenInNewTab".L();
						FileListView.DragFilesPreview.DragDropEffect = DragDropEffects.All;
					}
				}
			}
		}
	}

	private async void OnDrop(object args) {
		if (args is TabItemDragEventArgs ti) {
			var tab = (FileGridViewModel)ti.TabItem.DataContext;
			if (!CanDragDrop(ti.DragEventArgs, tab)) {
				return;
			}
			FileUtils.HandleDrop(DataObjectContent.Parse(ti.DragEventArgs.Data), tab.FullPath, ti.DragEventArgs.Effects.GetFirstEffect());
		} else {
			var tb = (TabBorderDragEventArgs)args;
			if (tb.DragEventArgs.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } fileList) {
				foreach (var folderPath in fileList.Where(Directory.Exists)) {
					await OpenPathInNewTabAsync(folderPath);
				}
			}
		}
	}

	protected override void OnMouseEnter(MouseEventArgs e) {
		MouseOverTabControl = this;
		base.OnMouseEnter(e);
	}
}