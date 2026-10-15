namespace WinPilot.Test;

internal static class PluginConfigurationTestData
{
    internal const string Definition = """
        [
          {"name":"text","type":"text"},
          {"name":"toggle","type":"checkbox","default":true},
          {"name":"count","type":"number"},
          {"name":"choices","type":"multi-checkbox"},
          {"name":"fixed","type":"collection","mode":"fixed","items":[
            {"name":"group","fields":[{"name":"target","type":"text"},{"name":"enabled","type":"checkbox"}]}]},
          {"name":"rows","type":"collection","mode":"dynamic","item":{"fields":[
            {"name":"text","type":"text"},{"name":"enabled","type":"checkbox","default":true}]}}
        ]
        """;
}
