// Copyright (C) 2026 Hyprism Launcher
// SPDX-License-Identifier: GPL-3.0-only

using Avalonia.Controls;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Hyprism.Desktop.Screens.Together;

public partial class TogetherView : UserControl
{
    private TogetherViewModel? _observed;
    private TogetherPlayerViewModel? _dragged;

    public TogetherView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ObserveViewModel();
        AttachedToVisualTree += (_, _) => RefreshPreview();
    }

    private void OnVibrationTestClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is TogetherViewModel viewModel && sender is Avalonia.Controls.Button { DataContext: TogetherPlayerViewModel player })
            viewModel.TestVibrationCommand.Execute(player);
    }

    private void ObserveViewModel()
    {
        if (_observed is not null)
        {
            _observed.PropertyChanged -= OnViewModelPropertyChanged;
            _observed.Players.CollectionChanged -= OnPlayersChanged;
        }
        _observed = DataContext as TogetherViewModel;
        if (_observed is not null)
        {
            _observed.PropertyChanged += OnViewModelPropertyChanged;
            _observed.Players.CollectionChanged += OnPlayersChanged;
        }
        RefreshPreview();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e) => RefreshPreview();
    private void OnPlayersChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshPreview();

    private void RefreshPreview()
    {
        if (LayoutPreview is null || _observed is null) return;
        LayoutPreview.Children.Clear();
        var count = _observed.Players.Count;
        for (var i = 0; i < count; i++)
        {
            var (x, y, width, height) = TogetherLayoutCalculator.GetPreviewBounds(i, count, _observed.Layout, LayoutPreview.Width, LayoutPreview.Height);
            var player = _observed.Players[i];
            var tile = new Border
            {
                Width = width - 6,
                Height = height - 6,
                Background = new SolidColorBrush(Color.FromRgb(39, 71, 91)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(81, 179, 222)),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(8),
                Child = new TextBlock
                {
                    Text = $"P{player.Slot} · {player.SelectedProfile?.Name ?? "Player"}",
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center
                },
                Tag = player,
                Cursor = new Cursor(StandardCursorType.SizeAll)
            };
            Canvas.SetLeft(tile, x + 3);
            Canvas.SetTop(tile, y + 3);
            tile.PointerPressed += OnPreviewPointerPressed;
            tile.PointerReleased += OnPreviewPointerReleased;
            LayoutPreview.Children.Add(tile);
        }
    }

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { Tag: TogetherPlayerViewModel player } tile && DataContext is TogetherViewModel { CanEditSession: true })
        {
            _dragged = player;
            e.Pointer.Capture(tile);
            e.Handled = true;
        }
    }

    private void OnPreviewPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragged is null || DataContext is not TogetherViewModel viewModel) return;
        var position = e.GetPosition(LayoutPreview);
        var target = Enumerable.Range(0, viewModel.Players.Count)
            .OrderBy(index =>
            {
                var (x, y, width, height) = TogetherLayoutCalculator.GetPreviewBounds(index, viewModel.Players.Count, viewModel.Layout, LayoutPreview.Width, LayoutPreview.Height);
                return Math.Pow(position.X - (x + width / 2), 2) + Math.Pow(position.Y - (y + height / 2), 2);
            }).FirstOrDefault();
        viewModel.MovePlayerTo(_dragged, target);
        e.Pointer.Capture(null);
        _dragged = null;
        e.Handled = true;
    }
}

public static class TogetherLayoutCalculator
{
    public static (double X, double Y, double Width, double Height) GetPreviewBounds(
        int index, int count, string layout, double width, double height)
    {
        if (count is < 1 or > 4 || index < 0 || index >= count) throw new ArgumentOutOfRangeException(nameof(index));
        if (count == 2 && layout == "SideBySide") return (width / 2 * index, 0, width / 2, height);
        if (count == 2) return (0, height / 2 * index, width, height / 2);
        if (count == 3 && layout == "Asymmetric")
            return index == 0 ? (0, 0, width / 2, height) : (width / 2, height / 2 * (index - 1), width / 2, height / 2);
        if (count == 1) return (0, 0, width, height);
        return (width / 2 * (index % 2), height / 2 * (index / 2), width / 2, height / 2);
    }
}
