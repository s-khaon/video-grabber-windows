﻿using System.Windows;
using System.Windows.Input;
using VideoGrabber.App.ViewModels;
using VideoGrabber.Core.Abstractions;
using VideoGrabber.Core.Services;

namespace VideoGrabber.App;

public partial class AddDownloadWindow : Window
{
    private readonly AddDownloadViewModel _viewModel;

    public AddDownloadWindow(
        DownloadManager downloads,
        AppSettings settings,
        IFilePicker filePicker,
        IClipboardService clipboard)
    {
        InitializeComponent();
        _viewModel = new AddDownloadViewModel(downloads, settings, filePicker, clipboard);
        _viewModel.CloseRequested += () => DialogResult = true;
        DataContext = _viewModel;
        Loaded += (_, _) => Keyboard.Focus(this);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
