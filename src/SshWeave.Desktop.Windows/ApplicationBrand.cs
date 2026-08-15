using Aprillz.MewUI;

namespace SshWeave.Desktop.Windows;

internal static class ApplicationBrand
{
    // 显式资源名保持 Native AOT 兼容，同一图形同时用于窗口、界面和安装资产。
    public static IconSource Icon { get; } = IconSource.FromResource<ResourceMarker>("SshWeave.Assets.Icon.ico");

    public static ImageSource Logo { get; } = ImageSource.FromResource<ResourceMarker>("SshWeave.Assets.Logo.png");

    // MewUI 的泛型资源加载器需要非静态类型作为程序集定位标记。
    private sealed class ResourceMarker;
}
