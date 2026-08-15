using System.Xml.Linq;

namespace SshWeave.Tests;

public sealed class DesktopManifestTests
{
    [Fact]
    public void DesktopAlwaysRequestsAdministratorToken()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "SshWeave.Desktop.Windows.app.manifest");
        XDocument manifest = XDocument.Load(path);
        XNamespace trust = "urn:schemas-microsoft-com:asm.v3";

        XElement executionLevel = Assert.Single(manifest.Descendants(trust + "requestedExecutionLevel"));

        // 透明 TCP 的网卡和活动路由在连接前创建，桌面进程必须从入口即持有管理员令牌。
        Assert.Equal("requireAdministrator", (string?)executionLevel.Attribute("level"));
        Assert.Equal("false", (string?)executionLevel.Attribute("uiAccess"));
    }
}
