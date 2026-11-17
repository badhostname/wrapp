// Resolve ambiguities between WPF and WinForms types.
// UseWindowsForms=true is needed for FolderBrowserDialog.
global using Application = System.Windows.Application;
global using UserControl = System.Windows.Controls.UserControl;
global using DragEventArgs = System.Windows.DragEventArgs;
global using DragDropEffects = System.Windows.DragDropEffects;
global using DataFormats = System.Windows.DataFormats;
global using Visibility = System.Windows.Visibility;
global using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
global using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
global using MessageBox = System.Windows.MessageBox;
global using MessageBoxButton = System.Windows.MessageBoxButton;
global using MessageBoxImage = System.Windows.MessageBoxImage;
