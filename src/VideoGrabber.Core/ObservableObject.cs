using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VideoGrabber.Core;

/// <summary>
/// 最小可观察对象实现。放在 Core 里是为了让模型不依赖任何 UI 框架；
/// WPF 数据绑定只需要 INotifyPropertyChanged。
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
