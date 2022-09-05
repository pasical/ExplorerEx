﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using ExplorerEx.Command;
using ExplorerEx.Utils;
using ExplorerEx.View.Controls;
using Microsoft.Win32;

namespace ExplorerEx;

internal class SettingsCategory {
	public SettingsCategory(string header) {
		Header = header;
	}

	public string Header { get; }

	public List<SettingsCategoryItem> Items { get; } = new();
}

internal abstract class SettingsCategoryItem {
	protected SettingsCategoryItem(string header, string? description, string? icon) {
		Header = header;
		Description = description;
		if (icon != null) {
			Icon = Application.Current.Resources[icon] as ImageSource;
		}
	}

	public string Header { get; }

	public string? Description { get; }

	public ImageSource? Icon { get; }
}

internal class SettingsExpander : SettingsCategoryItem {
	public SettingsExpander(string header, string? description, string? icon) : base(header, description, icon) { }

	public List<SettingsItem> Items { get; } = new();
}

internal enum SettingsType {
	Unknown,
	Boolean,
	Number,
	String,
	Select,
}

internal abstract class SettingsItem : SettingsCategoryItem, INotifyPropertyChanged {
	private class EmptySettingsItem : SettingsItem {
		public EmptySettingsItem() : base(null!, null!, null, null, SettingsType.Unknown) { }
	}

	internal static SettingsItem Empty { get; } = new EmptySettingsItem();

	protected SettingsItem(string fullName, string header, string? description, string? icon, SettingsType type) : base(header, description, icon) {
		FullName = fullName;
		Type = type;
		Self = this;
	}

	public string FullName { get; }

	public SettingsType Type { get; }

	public object? Value {
		get => value;
		set {
			if (this.value != value) {
				if (Changing?.Invoke(value) ?? true) {
					this.value = value;
					OnPropertyChanged();
					OnPropertyChanged(nameof(Self));
					if (value != null) {
						ConfigHelper.SaveToBuffer(FullName, value);
					} else {
						ConfigHelper.Delete(FullName);
					}
					Changed?.Invoke(value);
				} else {
					OnPropertyChanged();
					OnPropertyChanged(nameof(Self));
				}
			}
		}
	}

	private object? value;

	/// <summary>
	/// 用于Binding
	/// </summary>
	public SettingsItem Self { get; }

	public virtual void SetDefaultValue(object? value) {
		Value = value;
	}

	public bool GetBoolean() => Convert.ToBoolean(Value);

	public int GetInt32() => Convert.ToInt32(Value);

	public double GetDouble() => Convert.ToDouble(Value);

	public string GetString() => Convert.ToString(Value) ?? string.Empty;

	/// <summary>
	/// 返回值设为false表示取消
	/// </summary>
	public event Func<object?, bool>? Changing;

	/// <summary>
	/// 与<see cref="PropertyChanged"/>不同，这个是专门用于监测值的变化
	/// </summary>
	public event Action<object?>? Changed;

	public event PropertyChangedEventHandler? PropertyChanged;

	private void OnPropertyChanged([CallerMemberName] string? propertyName = null) {
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}
}

internal class SettingsSelectItem : SettingsItem {
	public class Item {
		public Item(string header, int value) {
			Header = header.L();
			Value = value;
		}

		public string Header { get; }

		public int Value { get; }

		public override string ToString() {
			return Header;
		}
	}

	public List<Item> Items { get; } = new();

	public int SelectedIndex {
		get => Value is int value ? Items.FindIndex(i => i.Value == value) : -1;
		set {
			if (value < 0 || value >= Items.Count) {
				Value = null;
			} else {
				Value = Items[value].Value;
			}
		}
	}

	public SettingsSelectItem(string fullName, string header, string? description, string? icon) : base(fullName, header, description, icon, SettingsType.Select) { }

	public override void SetDefaultValue(object? value) {
		if (value is int i) {
			Value = i;
		} else {
			Value = 0;
		}
	}
}

internal class SettingsBooleanItem : SettingsItem {
	public SettingsBooleanItem(string fullName, string header, string? description, string? icon) : base(fullName, header, description, icon, SettingsType.Boolean) { }
}

internal class SettingsNumberItem : SettingsItem {
	public double Min { get; }

	public double Max { get; }

	public SettingsNumberItem(string fullName, string header, double min, double max, string? description, string? icon) : base(fullName, header, description, icon, SettingsType.Number) {
		Min = min;
		Max = max;
	}

	public override void SetDefaultValue(object? value) {
		if (value != null && double.TryParse(value.ToString(), out var number) && number <= Max && number >= Min) {
			Value = number;
		} else {
			Value = Max;
		}
	}
}

internal class SettingsStringItem : SettingsItem {
	public SimpleCommand? BrowserFileCommand { get; set; }

	public SettingsStringItem(string fullName, string header, string? description, string? icon) : base(fullName, header, description, icon, SettingsType.String) { }

	public void SetBrowserFileCommand(string filter) {
		BrowserFileCommand = new SimpleCommand(() => {
			var ofd = new OpenFileDialog {
				CheckFileExists = true,
				Filter = filter
			};
			if (ofd.ShowDialog().GetValueOrDefault()) {
				Value = ofd.FileName;
			}
		});
	}
}

public enum ColorMode {
	FollowSystem,
	Light,
	Dark
}

public enum WindowBackdrop {
	SolidColor,
	Acrylic,
	Mica
}

internal sealed class Settings : INotifyPropertyChanged {
	public static Settings Current { get; } = new();

	public ObservableCollection<SettingsCategory> Categories { get; } = new();

	private readonly Dictionary<string, SettingsItem> settings = new();

	#region 特殊/常用设置

	public static class CommonSettings {
		public const string Language = "Appearance.Language";
		public const string ColorMode = "Appearance.ColorMode";
		public const string WindowBackdrop = "Appearance.Background.WindowBackdrop";
		public const string BackgroundImage = "Appearance.Background.BackgroundImage";
		public const string BackgroundImageOpacity = "Appearance.Background.BackgroundImageOpacity";

		public const string DoubleClickGoUpperLevel = "Common.DoubleClickGoUpperLevel";

		public const string DontAskWhenClosingMultiTabs = "Customize.DontAskWhenClosingMultiTabs";
		public const string DontAskWhenChangeExtension = "Customize.DontAskWhenChangeExtension";
		public const string DontAskWhenRecycle = "Customize.DontAskWhenRecycle";
		public const string DontAskWhenDelete = "Customize.DontAskWhenDelete";

		public const string ShowHiddenFilesAndFolders = "Advanced.ShowHiddenFilesAndFolders";
		public const string ShowProtectedSystemFilesAndFolders = "Advanced.ShowProtectedSystemFilesAndFolders";

		public const string SetExplorerExAsDefault = "Experimental.SetExplorerExAsDefault";
	}

	public static CultureInfo CurrentCulture { get; private set; } = CultureInfo.CurrentUICulture;

	public static event Action? ThemeChanged;

	/// <summary>
	/// 是否为暗色模式，根据设置获取，如果是跟随系统，就获取系统色
	/// </summary>
	public bool IsDarkMode {
		get {
			var colorMode = (ColorMode)settings[CommonSettings.ColorMode].GetInt32();
			switch (colorMode) {
			case ColorMode.FollowSystem:
				try {
					using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
					return key?.GetValue("AppsUseLightTheme") is 0;
				} catch {
					return false;
				}
			case ColorMode.Light:
				return false;
			case ColorMode.Dark:
				return true;
			default:
				return false;
			}
		}
	}

	public WindowBackdrop WindowBackdrop => (WindowBackdrop)settings[CommonSettings.WindowBackdrop].GetInt32();

	public ImageSource? BackgroundImage { get; set; }

	public double BackgroundImageOpacity => this[CommonSettings.BackgroundImageOpacity].GetDouble();

	private void BackgroundImage_OnChanged(object? o) {
		if (o is string path && File.Exists(path)) {
			try {
				BackgroundImage = new BitmapImage(new Uri(path));
			} catch {
				BackgroundImage = null;
			}
		} else {
			BackgroundImage = null;
		}
		OnPropertyChanged(nameof(BackgroundImage));
	}

	private static bool SetExplorerExAsDefault_OnChanging(object? o) {
		var executingAsm = Assembly.GetExecutingAssembly();
		var executingPath = Path.GetDirectoryName(executingAsm.Location)!;
		if (o is true) {
			ConfigHelper.Save("Path", Path.ChangeExtension(executingAsm.Location, ".exe"));
			// 务必检查两个dll的有效性
			var proxyDllPath = Path.Combine(executingPath, "ExplorerProxy.dll");
			if (!File.Exists(proxyDllPath)) {
				ContentDialog.Error("#SetExplorerExAsDefaultFailProxyNotFound".L());
				return false;
			}
			if (!File.Exists(Path.Combine(executingPath, "Interop.SHDocVw.dll"))) {
				ContentDialog.Error("#SetExplorerExAsDefaultFailSHDocVwNotFound".L());
				return false;
			}
			if (ContentDialog.Ask("#SetExplorerExAsDefaultInstructions0".L(), "PleaseReadCarefully".L()) != ContentDialog.ContentDialogResult.Primary) {
				return false;
			}
			try {
				using var stream = executingAsm.GetManifestResourceStream("ExplorerEx.Assets.DeleteExplorerExProxy.reg");
				if (stream == null) {
					ContentDialog.Error("#SetExplorerExAsDefaultFailReg".L());
					return false;
				}
				var regFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Delete ExplorerEx Proxy.reg");
				using var fs = new FileStream(regFilePath, FileMode.Create);
				stream.CopyTo(fs);
			} catch {
				ContentDialog.Error("#SetExplorerExAsDefaultFailReg".L());
				return false;
			}
			if (ContentDialog.Ask("#SetExplorerExAsDefaultInstructions1".L(), "PleaseReadCarefully".L()) != ContentDialog.ContentDialogResult.Primary) {
				return false;
			}
			if (ContentDialog.Ask("#SetExplorerExAsDefaultInstructions2".L(), "PleaseReadCarefully".L()) != ContentDialog.ContentDialogResult.Primary) {
				return false;
			}
			try {
				using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ExplorerEx.Assets.SetExplorerExProxy.reg");
				if (stream == null) {
					ContentDialog.Error("#SetExplorerExAsDefaultFailReg".L());
					return false;
				}
				var setRegString = new StreamReader(stream).ReadToEnd().Replace("REPLACE_HERE", proxyDllPath.Replace('\\', '/'));
				var regFilePath = Path.Combine(executingPath, "SetExplorerExProxy.reg");
				File.WriteAllText(regFilePath, setRegString);
				Process.Start(new ProcessStartInfo {
					FileName = "regedit",
					Arguments = "/S " + regFilePath,
					UseShellExecute = true
				});
			} catch {
				ContentDialog.Error("#SetExplorerExAsDefaultFailReg".L());
				return false;
			}
		} else {
			try {
				// 先尝试自动运行Reg
				using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ExplorerEx.Assets.DeleteExplorerExProxy.reg");
				if (stream == null) {
					ContentDialog.Error("#UnsetExplorerExAsDefaultFailReg".L());
					return false;
				}
				var regFilePath = Path.Combine(executingPath, "UnsetExplorerExProxy.reg");
				using (var fs = new FileStream(regFilePath, FileMode.Create)) {
					stream.CopyTo(fs);
				}
				Process.Start(new ProcessStartInfo {
					FileName = "regedit",
					Arguments = "/S " + regFilePath,
					UseShellExecute = true
				});
			} catch {
				// 自动运行失败，改为手动运行
				if (ContentDialog.Ask("#UnsetExplorerExAsDefaultInstructions".L(), "PleaseReadCarefully".L()) != ContentDialog.ContentDialogResult.Primary) {
					return true;
				}
				try {
					using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ExplorerEx.Assets.DeleteExplorerExProxy.reg");
					if (stream == null) {
						ContentDialog.Error("#UnsetExplorerExAsDefaultFailReg".L());
						return false;
					}
					var regFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Delete ExplorerEx Proxy.reg");
					using var fs = new FileStream(regFilePath, FileMode.Create);
					stream.CopyTo(fs);
				} catch {
					ContentDialog.Error("#UnsetExplorerExAsDefaultFailReg".L());
				}
			}
		}
		return true;
	}

	private void RegisterEvents() {
		ThemeChanged = null;

		settings[CommonSettings.Language].Changed += o => {  // 更改界面语言
			if (o is int lcId) {
				var prevCulture = CurrentCulture;
				try {
					CurrentCulture = new CultureInfo(lcId);
				} catch {
					CurrentCulture = prevCulture;
				}
			}
		};
		settings[CommonSettings.WindowBackdrop].Changed += _ => ThemeChanged?.Invoke();

		settings[CommonSettings.BackgroundImage].Changed += BackgroundImage_OnChanged;
		BackgroundImage_OnChanged(settings[CommonSettings.BackgroundImage].Value);

		settings[CommonSettings.BackgroundImageOpacity].Changed += _ => OnPropertyChanged(nameof(BackgroundImageOpacity));
		settings[CommonSettings.ColorMode].Changed += _ => ThemeChanged?.Invoke();  // 更改颜色

		settings[CommonSettings.SetExplorerExAsDefault].Changing += SetExplorerExAsDefault_OnChanging;
	}

	#endregion

	public SettingsItem this[string name] {
		get {
			if (settings.TryGetValue(name, out var value)) {
				return value;
			}
			return SettingsItem.Empty;
		}
	}

	public void LoadSettings() {
		var lcId = ConfigHelper.LoadInt(CommonSettings.Language, -1);
		if (lcId != -1) {
			CurrentCulture = new CultureInfo(lcId);
		}

		using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ExplorerEx.Assets.Settings.xml")!;
		using var xml = XmlReader.Create(stream);
		if (!xml.Read() || xml.Name != "settings") {
			return;
		}

		SettingsCategory? category = null;
		SettingsExpander? expander = null;
		SettingsItem? item = null;
		while (xml.Read()) {
			string? header;
			switch (xml.Name) {
			case "category":
				if (xml.NodeType == XmlNodeType.EndElement && category != null) {
					Categories.Add(category);
					category = null;
				} else if ((header = xml.GetAttribute("header")) != null) {
					category = new SettingsCategory(header);
				}
				break;
			case "expander":
				if (xml.NodeType == XmlNodeType.EndElement) {
					expander = null;
				} else if (category != null && (header = xml.GetAttribute("header")) != null) {
					expander = new SettingsExpander(header, xml.GetAttribute("description"), xml.GetAttribute("icon"));
					category.Items.Add(expander);
				}
				break;
			case "item":
				if (xml.NodeType == XmlNodeType.EndElement) {
					item = null;
				} else if (category != null && (header = xml.GetAttribute("header")) != null) {
					if (!Enum.TryParse<SettingsType>(xml.GetAttribute("type"), true, out var type)) {
						continue;
					}
					string fullName;
					if (expander != null) {
						fullName = category.Header + '.' + expander.Header + '.' + header;
					} else {
						fullName = category.Header + '.' + header;
					}
					switch (type) {
					case SettingsType.Boolean:
						item = new SettingsBooleanItem(fullName, header, xml.GetAttribute("description"), xml.GetAttribute("icon"));
						break;
					case SettingsType.Number:
						if (!double.TryParse(xml.GetAttribute("min"), out var min)) {
							min = 0;
						}
						if (!double.TryParse(xml.GetAttribute("max"), out var max)) {
							max = 0;
						}
						item = new SettingsNumberItem(fullName, header, min, max, xml.GetAttribute("description"), xml.GetAttribute("icon"));
						break;
					case SettingsType.String:
						item = new SettingsStringItem(fullName, header, xml.GetAttribute("description"), xml.GetAttribute("icon"));
						break;
					case SettingsType.Select:
						item = new SettingsSelectItem(fullName, header, xml.GetAttribute("description"), xml.GetAttribute("icon"));
						break;
					default:
						continue;
					}
					item.SetDefaultValue(ConfigHelper.Load(fullName) ?? xml.GetAttribute("default"));
					if (expander != null) {
						expander.Items.Add(item);
					} else {
						category.Items.Add(item);
					}
					settings.Add(fullName, item);
				}
				break;
			case "option": {
				if (xml.NodeType == XmlNodeType.Element && item is SettingsSelectItem ssi && (header = xml.GetAttribute("header")) != null) {
					if (!int.TryParse(xml.GetAttribute("value"), out var value)) {
						value = ssi.Items.Count;
					}
					ssi.Items.Add(new SettingsSelectItem.Item(header, value));
					ssi.Value ??= ssi.Items[0];
				}
				break;
			}
			case "browse": {
				if (xml.NodeType == XmlNodeType.Element && item is SettingsStringItem ssi) {
					ssi.SetBrowserFileCommand(xml.GetAttribute("filter") ?? "*.*|*.*");
				}
				break;
			}
			}
		}

		// 调整语言
		if (lcId == -1) {
			settings[CommonSettings.Language].Value = CurrentCulture.LCID;
		}
		// 设置窗口背景材质
		if (ConfigHelper.Load(CommonSettings.WindowBackdrop) == null) {
			if (Environment.OSVersion.Version >= Version.Parse("10.0.22000.0")) {
				this[CommonSettings.WindowBackdrop].Value = 2;  // Mica
			} else {
				this[CommonSettings.WindowBackdrop].Value = 1;  // Acrylic
			}
		}
		// 设置是否为默认文件管理器的值
		var registryKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Browser Helper Objects\{11451400-8700-480c-a27f-000001919810}");
		if (registryKey != null) {
			this[CommonSettings.SetExplorerExAsDefault].Value = true;
			registryKey.Dispose();
		} else {
			this[CommonSettings.SetExplorerExAsDefault].Value = false;
		}

		RegisterEvents();
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	private void OnPropertyChanged([CallerMemberName] string? propertyName = null) {
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
	}
}