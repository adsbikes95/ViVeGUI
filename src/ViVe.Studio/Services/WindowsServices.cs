// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using Microsoft.Win32;
using ViVe.Studio.Services;

namespace ViVe.Studio.Platform;

public sealed class WindowsSystemInfoProvider : ISystemInfoProvider
{
    public SystemInfo GetSystemInfo()
    {
        var build = Environment.OSVersion.Version.Build;
        string product = "Windows";
        string? displayVersion = null;
        int? ubr = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key != null)
            {
                product = key.GetValue("ProductName") as string ?? product;
                displayVersion = key.GetValue("DisplayVersion") as string ?? key.GetValue("ReleaseId") as string;
                if (key.GetValue("UBR") is int revision) ubr = revision;
                if (int.TryParse(key.GetValue("CurrentBuildNumber") as string, out var regBuild)) build = regBuild;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        // The registry ProductName still says "Windows 10" on Windows 11.
        if (build >= 22000 && product.StartsWith("Windows 10", StringComparison.OrdinalIgnoreCase))
            product = "Windows 11" + product.Substring("Windows 10".Length);
        return new SystemInfo(product, displayVersion, build, ubr,
            RuntimeInformation.OSArchitecture.ToString(), RuntimeInformation.ProcessArchitecture.ToString(), OperatingSystem.IsWindows());
    }
}

public sealed class WindowsElevationService : IElevationService
{
    private const int ERROR_CANCELLED = 1223;

    public bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public bool TryRestartElevated()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            // Only our own executable is started; no arguments are forwarded.
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas" });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_CANCELLED)
        {
            return false;
        }
        Application.Current.Shutdown();
        return true;
    }
}

public sealed class WpfDialogService : IDialogService
{
    public bool Confirm(string title, string message, bool highRisk) =>
        MessageBox.Show(Owner(), message, title, MessageBoxButton.YesNo, highRisk ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No)
        == MessageBoxResult.Yes;

    public void ShowMessage(string title, string message, bool isError) =>
        MessageBox.Show(Owner(), message, title, MessageBoxButton.OK, isError ? MessageBoxImage.Error : MessageBoxImage.Information);

    public string? PickSaveFile(string title, string filter, string defaultFileName)
    {
        var dialog = new SaveFileDialog { Title = title, Filter = filter, FileName = defaultFileName, AddExtension = true, OverwritePrompt = true };
        return dialog.ShowDialog(Owner()) == true ? dialog.FileName : null;
    }

    public string? PickOpenFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog(Owner()) == true ? dialog.FileName : null;
    }

    public string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        return dialog.ShowDialog(Owner()) == true ? dialog.FolderName : null;
    }

    private static Window Owner() => Application.Current.MainWindow;
}

public sealed class WpfThemeService : IThemeService
{
    public void Apply(ThemePreference preference)
    {
        Application.Current.ThemeMode = preference switch
        {
            ThemePreference.Light => ThemeMode.Light,
            ThemePreference.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
    }
}
