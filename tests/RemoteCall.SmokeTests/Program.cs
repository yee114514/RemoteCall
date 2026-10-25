using RemoteCall.SmokeTests;

var tests = new (string Name, Func<TestEnvironment, Task> Body)[]
{
    ("密码哈希", Tests.PasswordHashingAsync),
    ("设置读写与校验", Tests.SettingsAsync),
    ("默认密码 admin", Tests.DefaultPasswordAsync),
    ("局域网地址过滤", Tests.LanAddressesAsync),
    ("不限制选项（-1）", Tests.UnlimitedAsync),
    ("设备名自动识别", Tests.DeviceNameAsync),
    ("静态页面", Tests.StaticPagesAsync),
    ("未登录跳转", Tests.RedirectAsync),
    ("登录与会话", Tests.LoginAsync),
    ("Cookie 属性与注销", Tests.CookieAsync),
    ("登录限流", Tests.LoginRateLimitAsync),
    ("发送消息", Tests.SendMessageAsync),
    ("完成 / 撤销完成", Tests.CompleteMessageAsync),
    ("设备改名", Tests.RenameDeviceAsync),
    ("管理页状态", Tests.AdminStateAsync),
    ("管理页改密码", Tests.AdminPasswordAsync),
    ("管理页消息操作", Tests.AdminMessagesAsync),
    ("管理页设置", Tests.AdminSettingsAsync),
    ("HTTP 协议细节", Tests.ProtocolAsync),
    ("存储与裁剪", Tests.StoreAsync),
};

var passed = 0;
var failures = new List<string>();

foreach (var (name, body) in tests)
{
    TestEnvironment env;
    try
    {
        env = await TestEnvironment.CreateAsync();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR {name}: 无法创建测试环境：{ex.Message}");
        failures.Add(name);
        continue;
    }

    await using (env)
    {
        try
        {
            await body(env);
            Console.WriteLine($"PASS  {name}");
            passed++;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL  {name}: {ex.Message}");
            failures.Add(name);
        }
    }
}

Console.WriteLine();
Console.WriteLine($"结果：{passed} 通过 / {failures.Count} 失败（共 {tests.Length} 项）");
if (failures.Count > 0)
{
    Console.WriteLine("失败项：" + string.Join("、", failures));
    return 1;
}

Console.WriteLine("全部冒烟测试通过。");
return 0;
