using System.Text.Json;
using SideBySide.Modules.LanShare;

namespace SideBySide.Modules.Tests;

public class LanShareTests
{
    /// <summary>독립 앱(lan_share_setup.py)과 같은 상수여야 두 도구로 각각 설정한 PC끼리 연결된다.</summary>
    [Fact]
    public void Constants_match_standalone_app()
    {
        Assert.Equal("192.168.1.1", LanShareCore.Pc1Ip);
        Assert.Equal("192.168.1.2", LanShareCore.Pc2Ip);
        Assert.Equal("255.255.255.0", LanShareCore.SubnetMask);
        Assert.Equal("8.8.8.8", LanShareCore.Dns);
        Assert.Equal("LAN_Share", LanShareCore.ShareName);
        Assert.Equal(@"C:\LAN_Share", LanShareCore.DefaultFolder);
        Assert.Equal("LAN Direct Share", LanShareCore.Remark);
        Assert.Equal(@"\\192.168.1.2\LAN_Share", LanShareCore.PeerSharePath(1));
        Assert.Equal(@"\\192.168.1.1\LAN_Share", LanShareCore.PeerSharePath(2));
    }

    [Fact]
    public void Apply_commands_have_same_effect_as_standalone()
    {
        var steps = LanShareCore.BuildApply(12, 2, @"C:\LAN_Share", SharePermission.Change, "Everyone", shareExists: false);
        var all = steps.Select(s => string.Join(' ', s.Arguments)).ToList();

        Assert.All(steps, s => Assert.True(Path.IsPathFullyQualified(s.Program))); // System32 절대 경로, 셸 없음
        Assert.Contains(all, a => a.Contains("set address") && a.Contains("name=12") && a.Contains("address=192.168.1.2") && a.Contains("mask=255.255.255.0"));
        Assert.Contains(all, a => a.Contains("set dnsservers") && a.Contains("address=8.8.8.8"));
        Assert.Contains(all, a => a.Contains("group=" + LanShareCore.FileSharingGroup) && a.Contains("enable=Yes"));
        Assert.Contains(all, a => a.Contains("group=" + LanShareCore.NetworkDiscoveryGroup) && a.Contains("enable=Yes"));
        var share = steps.Single(s => s.Arguments[0] == "share");
        Assert.Equal(["share", @"LAN_Share=C:\LAN_Share", "/GRANT:Everyone,CHANGE", "/REMARK:LAN Direct Share"], share.Arguments);
    }

    [Fact]
    public void Existing_share_is_left_untouched()
    {
        var steps = LanShareCore.BuildApply(3, 1, @"C:\LAN_Share", SharePermission.Read, "Everyone", shareExists: true);
        Assert.DoesNotContain(steps, s => s.Arguments[0] == "share");
    }

    [Fact]
    public void Folder_with_spaces_stays_one_argument()
    {
        var steps = LanShareCore.BuildApply(3, 1, @"D:\My Files\Share", SharePermission.Read, "Jeder", shareExists: false);
        var share = steps.Single(s => s.Arguments[0] == "share");
        Assert.Equal(@"LAN_Share=D:\My Files\Share", share.Arguments[1]);
        Assert.Equal("/GRANT:Jeder,READ", share.Arguments[2]);
    }

    [Fact]
    public void Reset_restores_previous_static_config_or_dhcp()
    {
        var dhcp = LanShareCore.BuildReset(5, null).Select(s => string.Join(' ', s.Arguments)).ToList();
        Assert.Contains(dhcp, a => a.Contains("set address") && a.Contains("source=dhcp"));
        Assert.Contains(dhcp, a => a.Contains("share LAN_Share /DELETE"));
        Assert.Contains(dhcp, a => a.Contains(LanShareCore.NetworkDiscoveryGroup) && a.Contains("enable=No"));

        var previous = new SavedIpConfig { Dhcp = false, Address = "10.0.0.5", Mask = "255.0.0.0", Gateway = "10.0.0.1", DnsDhcp = false, Dns = ["1.1.1.1", "9.9.9.9"] };
        var restore = LanShareCore.BuildReset(5, previous).Select(s => string.Join(' ', s.Arguments)).ToList();
        Assert.Contains(restore, a => a.Contains("address=10.0.0.5") && a.Contains("gateway=10.0.0.1"));
        Assert.Contains(restore, a => a.Contains("address=1.1.1.1"));
        Assert.Contains(restore, a => a.Contains("address=9.9.9.9"));
    }

    [Theory]
    [InlineData(@"C:\", false)]
    [InlineData(@"C:\Windows", false)]
    [InlineData(@"C:\Windows\Temp", false)]
    [InlineData(@"C:\Program Files\App", false)]
    [InlineData(@"relative\path", false)]
    [InlineData(@"\\server\share", false)]
    [InlineData("", false)]
    [InlineData(@"C:\LAN_Share", true)]
    [InlineData(@"C:\Work\Share", true)]
    public void Validates_share_folder(string folder, bool ok) => Assert.Equal(ok, LanShareCore.ValidateShareFolder(folder) is null);

    [Fact]
    public void User_profiles_root_cannot_be_shared()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.NotNull(LanShareCore.ValidateShareFolder(profile));
        Assert.NotNull(LanShareCore.ValidateShareFolder(Path.GetDirectoryName(profile)));
        Assert.Null(LanShareCore.ValidateShareFolder(Path.Combine(profile, "LAN_Share")));
    }

    [Theory]
    [InlineData("192.168.1.1", true)]
    [InlineData("1.2.3", false)]
    [InlineData("::1", false)]
    [InlineData("256.1.1.1", false)]
    [InlineData("1.1.1.1 & calc", false)]
    public void Validates_ipv4(string text, bool ok) => Assert.Equal(ok, LanShareCore.IsIPv4(text));

    [Fact]
    public void Elevated_operations_reject_unknown_adapter_without_running_commands()
    {
        var apply = JsonSerializer.Deserialize<StepsResponse>(LanShareElevated.Apply(JsonSerializer.Serialize(new ApplyRequest { AdapterIndex = -12345, Role = 1 })))!;
        Assert.NotNull(apply.Error);
        Assert.Empty(apply.Steps);

        var reset = JsonSerializer.Deserialize<StepsResponse>(LanShareElevated.Reset(JsonSerializer.Serialize(new ResetRequest { AdapterIndex = -12345 })))!;
        Assert.NotNull(reset.Error);
        Assert.Empty(reset.Steps);
    }

    [Fact]
    public void Adapter_list_excludes_wireless_and_virtual()
    {
        foreach (var a in LanShareCore.Adapters())
        {
            var text = (a.Name + " " + a.Description).ToLowerInvariant();
            Assert.DoesNotContain("wi-fi", text);
            Assert.DoesNotContain("vethernet", text);
            Assert.True(a.IPv4Index >= 0);
        }
    }
}
