// ============================================================================
// 全局别名
//
// <UseWindowsForms>true</UseWindowsForms> 会让 SDK 隐式引入
// System.Windows.Forms / System.Drawing，其中 Point、Color、Brush、Pen、
// Application、MouseEventArgs… 与 WPF 同名。
//
// 本项目是 WPF 应用，WinForms 只用来做托盘图标（NotifyIcon），
// 因此这里把不带前缀的名字统一指向 WPF 版本；
// 需要 WinForms 类型的地方一律写全名（System.Windows.Forms.NotifyIcon 等）。
// ============================================================================

global using Point = System.Windows.Point;
global using Size = System.Windows.Size;
global using Rect = System.Windows.Rect;
global using Vector = System.Windows.Vector;
global using Color = System.Windows.Media.Color;
global using Colors = System.Windows.Media.Colors;
global using Brush = System.Windows.Media.Brush;
global using Brushes = System.Windows.Media.Brushes;
global using Pen = System.Windows.Media.Pen;
global using FontFamily = System.Windows.Media.FontFamily;
global using ImageSource = System.Windows.Media.ImageSource;
global using Application = System.Windows.Application;
global using Clipboard = System.Windows.Clipboard;
global using MouseEventArgs = System.Windows.Input.MouseEventArgs;
global using KeyEventArgs = System.Windows.Input.KeyEventArgs;
global using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
global using MessageBox = System.Windows.MessageBox;
global using MessageBoxButton = System.Windows.MessageBoxButton;
global using MessageBoxImage = System.Windows.MessageBoxImage;
global using Cursor = System.Windows.Input.Cursor;
global using Cursors = System.Windows.Input.Cursors;
global using HorizontalAlignment = System.Windows.HorizontalAlignment;
global using VerticalAlignment = System.Windows.VerticalAlignment;
global using Visibility = System.Windows.Visibility;
global using FlowDirection = System.Windows.FlowDirection;
global using DragDropEffects = System.Windows.DragDropEffects;
global using DataFormats = System.Windows.DataFormats;

// 领域枚举 vs System.Threading.Tasks.TaskStatus —— 统一指向本项目版本。
global using TaskStatus = MaggotTask.Core.TaskStatus;

// WinForms 隐式 using 会顶掉 System.IO 的 Path / File / Directory 解析，
// 这里显式补回来（GDI+ 的 System.Drawing 类型在 TrayIconFactory 里写全名）。
global using Path = System.IO.Path;
global using File = System.IO.File;
global using Directory = System.IO.Directory;
global using FileInfo = System.IO.FileInfo;
global using DirectoryInfo = System.IO.DirectoryInfo;
global using FileStream = System.IO.FileStream;
global using MemoryStream = System.IO.MemoryStream;
global using Stream = System.IO.Stream;
global using StreamReader = System.IO.StreamReader;
global using StreamWriter = System.IO.StreamWriter;
global using TextWriter = System.IO.TextWriter;
global using IOException = System.IO.IOException;
global using StringWriter = System.IO.StringWriter;
global using FileMode = System.IO.FileMode;
global using FileAccess = System.IO.FileAccess;
global using FileShare = System.IO.FileShare;
global using SeekOrigin = System.IO.SeekOrigin;
