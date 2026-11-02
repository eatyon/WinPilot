namespace WinPilot.PluginContract;

/// <summary>
/// C# 插件入口；宿主加载后调用 Start，插件通过上下文注册能力和需要随卸载释放的资源。
/// </summary>
public interface ICSharpPlugin
{
    void Start(CSharpPluginContext context);
}
