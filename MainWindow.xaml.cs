using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Win32;

namespace RelayRdp;

public partial class MainWindow : Window
{
    private readonly ProfileStore store;
    private readonly string sessionsDirectory;
    private List<ConnectionProfile> savedProfiles = new();
    private ConnectionProfile draft = new();
    private string original = "";
    private Guid? selectedId;
    private bool updatingSelection;

    public MainWindow()
    {
        var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RelayRdp");
        store = new(dataDirectory);
        sessionsDirectory = Path.Combine(dataDirectory, "Sessions");
        InitializeComponent();

        BeginEditing(null);
        Run(() =>
        {
            ReloadProfiles(store.Load());
            BeginEditing(savedProfiles.FirstOrDefault());
        });
        try
        {
            RdpFile.CleanSessions(sessionsDirectory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            System.Diagnostics.Trace.TraceWarning("Папка сеансов Relay: {0}", error.Message);
        }
    }

    private void BeginEditing(ConnectionProfile? profile)
    {
        selectedId = profile?.Id;
        draft = profile?.Copy() ?? new ConnectionProfile();
        original = store.Snapshot(draft);
        DataContext = draft;
        PageTitle.Text = profile?.Name ?? "Новое подключение";
        PageSubtitle.Text = profile?.Address ?? "Параметры сеанса";
        DeleteMenuItem.IsEnabled = profile is not null;
        SelectProfile(profile);
    }

    private void SelectProfile(ConnectionProfile? profile)
    {
        updatingSelection = true;
        try
        {
            Profiles.SelectedItem = profile;
        }
        finally
        {
            updatingSelection = false;
        }
    }

    private void ReloadProfiles(List<ConnectionProfile> profiles)
    {
        savedProfiles = profiles.OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        updatingSelection = true;
        try
        {
            Profiles.ItemsSource = savedProfiles;
        }
        finally
        {
            updatingSelection = false;
        }

        NoProfiles.Visibility = savedProfiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Profiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingSelection || !ReferenceEquals(e.Source, Profiles)
            || Profiles.SelectedItem is not ConnectionProfile next || next.Id == selectedId) return;

        if (!CanLeaveDraft())
        {
            SelectProfile(savedProfiles.Find(profile => profile.Id == selectedId));
            return;
        }

        BeginEditing(savedProfiles.Find(profile => profile.Id == next.Id));
        SetStatus("");
    }

    private bool CanLeaveDraft()
    {
        if (store.Snapshot(draft) == original) return true;

        var answer = MessageBox.Show(this, "Сохранить изменения в профиле?", "Relay",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
        return answer == MessageBoxResult.No || (answer == MessageBoxResult.Yes && SaveProfile());
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!CanLeaveDraft()) e.Cancel = true;
        base.OnClosing(e);
    }

    private void NewCommand_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (!CanLeaveDraft()) return;
        BeginEditing(null);
        Settings.SelectedIndex = 0;
        SetStatus("");
        AddressInput.Focus();
    }

    private void SaveCommand_Executed(object sender, ExecutedRoutedEventArgs e) => SaveProfile();

    private bool SaveProfile() => Run(() =>
    {
        var profile = RdpFile.Normalize(draft);
        ReloadProfiles(store.Update(profile.Id, profile));
        BeginEditing(savedProfiles.Single(saved => saved.Id == profile.Id));
        SetStatus("Профиль сохранён.");
    });

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (!selectedId.HasValue) return;
        var answer = MessageBox.Show(this, $"Удалить профиль «{PageTitle.Text}»?", "Relay",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        Run(() =>
        {
            ReloadProfiles(store.Update(selectedId.Value, null));
            BeginEditing(savedProfiles.FirstOrDefault());
            SetStatus("Профиль удалён.");
        });
    }

    private void Connect_Click(object sender, RoutedEventArgs e) => Run(() =>
    {
        RdpFile.StartSession(sessionsDirectory, RdpFile.Build(draft));
        SetStatus("Клиент RDP запущен.");
    });

    private void More_Click(object sender, RoutedEventArgs e)
    {
        var menu = MoreButton.ContextMenu;
        menu.PlacementTarget = MoreButton;
        menu.CustomPopupPlacementCallback = (popup, button, offset) => new[]
        {
            new CustomPopupPlacement(new Point(button.Width - popup.Width, button.Height + 4), PopupPrimaryAxis.Horizontal)
        };
        menu.IsOpen = true;
    }

    private void Export_Click(object sender, RoutedEventArgs e) => Export(edit: false);
    private void ExportAndEdit_Click(object sender, RoutedEventArgs e) => Export(edit: true);

    private void Export(bool edit) => Run(() =>
    {
        var settings = RdpFile.Build(draft);
        var fileName = string.Concat((draft.Name ?? "").Trim().Split(Path.GetInvalidFileNameChars())).TrimEnd('.', ' ');
        var dialog = new SaveFileDialog
        {
            Title = "Экспорт подключения",
            FileName = fileName.Length > 0 ? fileName : "Relay",
            Filter = "Подключение к удалённому рабочему столу (*.rdp)|*.rdp",
            DefaultExt = ".rdp",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;
        RdpFile.Write(dialog.FileName, settings);
        SetStatus("Файл .rdp сохранён.");
        if (edit)
        {
            RdpFile.Launch(dialog.FileName, edit: true);
            SetStatus("Файл .rdp сохранён. Настройки Windows открыты.");
        }
    });

    private void OpenRdp_Click(object sender, RoutedEventArgs e) => OpenRdp(edit: false);
    private void EditRdp_Click(object sender, RoutedEventArgs e) => OpenRdp(edit: true);

    private void OpenRdp(bool edit) => Run(() =>
    {
        var dialog = new OpenFileDialog
        {
            Title = edit ? "Открыть настройки файла .rdp" : "Открыть файл подключения",
            Filter = "Подключение к удалённому рабочему столу (*.rdp)|*.rdp",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        RdpFile.Launch(dialog.FileName, edit);
        SetStatus(edit ? "Настройки Windows открыты." : "Клиент RDP запущен.");
    });

    private bool Run(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException
            or UnauthorizedAccessException or Win32Exception or SecurityException or SerializationException)
        {
            SetStatus(error.Message, isError: true);
            return false;
        }
    }

    private void SetStatus(string message, bool isError = false)
    {
        Status.Text = message;
        Status.Foreground = isError ? System.Windows.Media.Brushes.Firebrick
            : (System.Windows.Media.Brush)FindResource("Muted");
    }
}
