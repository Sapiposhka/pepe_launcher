using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace Pepe_Launcher
{
    public partial class MainWindow : Window
    {
        private List<GameItem> _allGames = new();
        private bool _isUpdateInProgress;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var list = await GameService.LoadGameListAsync();
                _allGames = list.Games;
                RefreshList();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось загрузить список игр: {ex.Message}", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RefreshList()
        {
            IEnumerable<GameItem> items = _allGames;
            if (OnlyInstalledCheckBox.IsChecked == true)
            {
                items = items.Where(g => g.IsInstalled);
            }

            GamesListView.ItemsSource = items.ToList();
        }

        private void FilterChanged(object sender, RoutedEventArgs e)
        {
            RefreshList();
        }

        private async void GameActionButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not GameItem game)
                return;

            if (!game.IsInstalled)
            {
                try
                {
                    ShowDownloadProgress($"Скачивание {game.Name}...");
                    btn.IsEnabled = false;
                    btn.Content = "Скачивание...";
                    var progress = new Progress<double>(p =>
                    {
                        DownloadProgressBar.Value = p;
                        DownloadStatusText.Text = $"Скачивание {game.Name}... {Math.Round(p)}%";
                    });
                    await GameService.InstallGameAsync(game, progress);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Не удалось установить игру: {ex.Message}", "Ошибка",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    HideDownloadProgress();
                    btn.IsEnabled = true;
                }

                RefreshList();
            }
            else
            {
                GameService.RunGame(game);
            }
        }

        private void DeleteGameButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not GameItem game)
                return;

            var result = MessageBox.Show(
                $"Точно хотите удалить игру \"{game.Name}\"?\nБудет удалена папка:\n{game.InstallFolder}",
                "Подтверждение удаления",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;

            try
            {
                GameService.DeleteGame(game);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось удалить игру: {ex.Message}", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }

            RefreshList();
            if (GamesListView.SelectedItem is GameItem selected && selected.Name == game.Name)
            {
                GamesListView.SelectedItem = null;
            }
        }

        private async void GamesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (GamesListView.SelectedItem is not GameItem game)
            {
                SelectedGameNameText.Text = "Выберите игру";
                SelectedGameImage.Source = null;
                return;
            }

            SelectedGameNameText.Text = game.Name;

            if (string.IsNullOrWhiteSpace(game.ImageUrl))
            {
                SelectedGameImage.Source = null;
                return;
            }

            try
            {
                // Если картинка на Яндекс Диске — скачиваем в локальный кэш и грузим с диска
                var pathOrUrl = await GameService.GetOrDownloadPreviewImagePathAsync(game);
                if (string.IsNullOrWhiteSpace(pathOrUrl))
                {
                    SelectedGameImage.Source = null;
                    return;
                }

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(pathOrUrl, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
                SelectedGameImage.Source = bitmap;
            }
            catch
            {
                SelectedGameImage.Source = null;
            }
        }

        private void ShowDownloadProgress(string initialText)
        {
            DownloadStatusText.Text = initialText;
            DownloadProgressBar.Value = 0;
            DownloadProgressBar.Visibility = Visibility.Visible;
        }

        private void HideDownloadProgress()
        {
            DownloadProgressBar.Visibility = Visibility.Collapsed;
            DownloadStatusText.Text = string.Empty;
            DownloadProgressBar.Value = 0;
        }

        private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isUpdateInProgress)
                return;

            try
            {
                _isUpdateInProgress = true;
                CheckUpdatesButton.IsEnabled = false;

                var check = await UpdateService.CheckForUpdatesAsync();
                if (!check.HasUpdate || check.Manifest is null || check.LatestVersion is null)
                {
                    MessageBox.Show(
                        $"Обновлений нет.\nТекущая версия: {FormatVersion(check.CurrentVersion)}",
                        "Обновление лаунчера",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                var changelogText = string.IsNullOrWhiteSpace(check.Manifest.Changelog)
                    ? "Список изменений не указан."
                    : check.Manifest.Changelog;

                var confirm = MessageBox.Show(
                    $"Найдена новая версия: {FormatVersion(check.LatestVersion)}\n" +
                    $"Текущая версия: {FormatVersion(check.CurrentVersion)}\n\n" +
                    $"Изменения:\n{changelogText}\n\n" +
                    "Скачать и установить обновление сейчас?",
                    "Обновление лаунчера",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (confirm != MessageBoxResult.Yes)
                    return;

                ShowDownloadProgress("Скачивание обновления...");
                var progress = new Progress<double>(p =>
                {
                    DownloadProgressBar.Value = p;
                    DownloadStatusText.Text = $"Скачивание обновления... {Math.Round(p)}%";
                });

                var updateZip = await UpdateService.DownloadUpdateAsync(check.Manifest, progress);
                DownloadStatusText.Text = "Запуск обновлятора...";

                UpdateService.LaunchUpdaterAndExit(updateZip, check.Manifest.LatestVersion);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось обновить лаунчер: {ex.Message}", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isUpdateInProgress = false;
                CheckUpdatesButton.IsEnabled = true;
                HideDownloadProgress();
            }
        }

        private static string FormatVersion(Version version)
        {
            var parts = new List<int> { version.Major, version.Minor };
            if (version.Build >= 0)
                parts.Add(version.Build);

            if (version.Revision > 0)
                parts.Add(version.Revision);

            return string.Join(".", parts);
        }
    }
}