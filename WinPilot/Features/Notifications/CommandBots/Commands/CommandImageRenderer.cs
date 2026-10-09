using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using WinPilot.Features.Plugins;
using WinPilot.Core.SystemStatus;

namespace WinPilot.Features.Notifications.CommandBots.Commands;

internal static class CommandImageRenderer
{
    internal sealed record Row(string Name, string Description, bool IsHeading = false);

    internal static byte[] RenderHelp(IReadOnlyList<CommandProcessor.CommandDefinition> commands, string prefix,
        IReadOnlyList<RegisteredPluginCommand>? plugins = null, string title = "WinPilot 命令帮助")
    {
        var rows = new List<Row>();
        if (commands.Count > 0 && plugins is { Count: > 0 }) rows.Add(new("WinPilot", "", true));
        rows.AddRange(commands.Select(command => new Row(prefix + command.Usage, command.Description)));
        if (plugins is not null)
            foreach (var group in plugins.GroupBy(command => command.PluginId))
            {
                rows.Add(new(group.First().PluginDisplayName, "", true));
                rows.AddRange(group.Select(command => new Row(prefix + command.Definition.Usage, command.Definition.Description)));
            }
        return Render(title, rows);
    }

    internal static byte[] RenderStatus(SystemResourceStatus status)
    {
        var rows = SystemStatusCommand.FormatRows(status);
        var gpuCount = Math.Max(1, status.GpuUsages.Count);
        var gpuRows = rows.Skip(2).Take(gpuCount).ToArray();
        var diskRows = rows.Skip(2 + gpuCount).ToArray();
        const int width = 760;
        const int panelWidth = 315;
        const int lowerTop = 305;
        const int rowHeight = 46;
        var lowerHeight = 76 + Math.Max(gpuRows.Length, diskRows.Length) * rowHeight;
        var height = lowerTop + lowerHeight + 76;
        if (height > 8000) throw new InvalidOperationException("图片内容过多");
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        bitmap.SetResolution(96, 96);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        graphics.Clear(Color.FromArgb(233, 238, 245));
        using var border = new Pen(Color.FromArgb(223, 227, 232));
        using var titleFont = new Font("Microsoft YaHei UI", 28, FontStyle.Bold, GraphicsUnit.Pixel);
        using var headingFont = new Font("Microsoft YaHei UI", 22, FontStyle.Bold, GraphicsUnit.Pixel);
        using var valueFont = new Font("Microsoft YaHei UI", 42, FontStyle.Bold, GraphicsUnit.Pixel);
        using var deviceFont = new Font("Microsoft YaHei UI", 21, FontStyle.Regular, GraphicsUnit.Pixel);
        using var footerFont = new Font("Microsoft YaHei UI", 17, FontStyle.Regular, GraphicsUnit.Pixel);
        using var text = new SolidBrush(Color.FromArgb(36, 39, 46));
        using var muted = new SolidBrush(Color.FromArgb(102, 112, 133));
        using var header = new SolidBrush(Color.FromArgb(234, 242, 252));
        using var accent = new SolidBrush(Color.FromArgb(76, 120, 168));
        using var panel = new SolidBrush(Color.FromArgb(250, 252, 255));
        using var card = Rounded(new Rectangle(24, 24, width - 48, height - 48), 10);
        graphics.FillPath(Brushes.White, card);
        graphics.DrawPath(border, card);
        using var title = Rounded(new Rectangle(55, 52, 650, 62), 6);
        graphics.FillPath(header, title);
        graphics.FillRectangle(accent, 55, 58, 4, 50);
        graphics.DrawString("WinPilot 系统状态", titleFont, text, 75, 64);
        using var leftAligned = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        using var rightAligned = new StringFormat(leftAligned) { Alignment = StringAlignment.Far };

        void DrawPanel(int x, int y, int panelHeight, string name)
        {
            using var shape = Rounded(new Rectangle(x, y, panelWidth, panelHeight), 6);
            graphics.FillPath(panel, shape);
            graphics.DrawPath(border, shape);
            graphics.DrawString(name, headingFont, text, new RectangleF(x + 20, y + 18, panelWidth - 40, 34), leftAligned);
        }

        void DrawSummary(int x, Row row)
        {
            DrawPanel(x, 137, 148, row.Name);
            graphics.DrawString(row.Description, valueFont, accent, new RectangleF(x + 20, 197, panelWidth - 40, 62), leftAligned);
        }

        void DrawDevices(int x, string name, IReadOnlyList<Row> devices)
        {
            DrawPanel(x, lowerTop, lowerHeight, name);
            for (var index = 0; index < devices.Count; index++)
            {
                var row = devices[index];
                var y = lowerTop + 66 + index * rowHeight;
                if (row.Name == name)
                    graphics.DrawString(row.Description, deviceFont, muted, new RectangleF(x + 20, y, panelWidth - 40, 34), leftAligned);
                else
                {
                    graphics.DrawString(row.Name, deviceFont, text, new RectangleF(x + 20, y, 160, 34), leftAligned);
                    graphics.DrawString(row.Description, deviceFont, muted, new RectangleF(x + 185, y, 110, 34), rightAligned);
                }
            }
        }

        DrawSummary(55, rows[0]);
        DrawSummary(390, rows[1]);
        DrawDevices(55, "GPU", gpuRows);
        DrawDevices(390, "磁盘", diskRows);
        graphics.DrawString("当前采样结果", footerFont, muted, 55, lowerTop + lowerHeight + 14);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    internal static byte[] RenderList(string titleText, IReadOnlyList<string> names, string usage)
    {
        const int width = 760;
        using var nameFont = new Font("Microsoft YaHei UI", 22, FontStyle.Regular, GraphicsUnit.Pixel);
        using var footerFont = new Font("Microsoft YaHei UI", 18, FontStyle.Regular, GraphicsUnit.Pixel);
        using var format = new StringFormat { Trimming = StringTrimming.None };
        using var measureBitmap = new Bitmap(1, 1);
        measureBitmap.SetResolution(96, 96);
        using var measure = Graphics.FromImage(measureBitmap);
        var rowHeights = names.Select(name => Math.Max(44,
            (int)Math.Ceiling(measure.MeasureString(name, nameFont, 610, format).Height) + 14)).ToArray();
        var usageHeight = Math.Max(30, (int)Math.Ceiling(measure.MeasureString(usage, footerFont, 610, format).Height));
        var height = 220 + rowHeights.Sum(rowHeight => rowHeight + 4) + usageHeight;
        if (height > 8000) throw new InvalidOperationException("图片内容过多");
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        bitmap.SetResolution(96, 96);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        graphics.Clear(Color.FromArgb(233, 238, 245));
        using var border = new Pen(Color.FromArgb(223, 227, 232));
        using var titleFont = new Font("Microsoft YaHei UI", 28, FontStyle.Bold, GraphicsUnit.Pixel);
        using var text = new SolidBrush(Color.FromArgb(36, 39, 46));
        using var muted = new SolidBrush(Color.FromArgb(102, 112, 133));
        using var header = new SolidBrush(Color.FromArgb(234, 242, 252));
        using var accent = new SolidBrush(Color.FromArgb(76, 120, 168));
        using var alternate = new SolidBrush(Color.FromArgb(250, 252, 255));
        using var card = Rounded(new Rectangle(24, 24, width - 48, height - 48), 10);
        graphics.FillPath(Brushes.White, card);
        graphics.DrawPath(border, card);
        using var title = Rounded(new Rectangle(55, 52, 650, 62), 6);
        graphics.FillPath(header, title);
        graphics.FillRectangle(accent, 55, 58, 4, 50);
        graphics.DrawString(titleText, titleFont, text, 75, 64);
        var y = 137;
        for (var index = 0; index < names.Count; index++)
        {
            graphics.FillRectangle(alternate, 55, y, 650, rowHeights[index]);
            graphics.DrawString(names[index], nameFont, text,
                new RectangleF(75, y + 7, 610, rowHeights[index] - 14), format);
            y += rowHeights[index] + 4;
        }
        graphics.DrawLine(border, 55, y + 12, 705, y + 12);
        graphics.DrawString(usage, footerFont, text, new RectangleF(75, y + 27, 610, usageHeight), format);
        graphics.DrawString("命令与名称之间的空格可省略", footerFont, muted, 75, y + 29 + usageHeight);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static byte[] Render(string titleText, IReadOnlyList<Row> rows)
    {
        const int width = 760;
        var contentBottom = rows.Count == 0 ? 114 : 137 + (rows.Count - 1) * 88 + 80;
        var height = contentBottom + 48;
        if (height > 8000) throw new InvalidOperationException("图片内容过多");
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        bitmap.SetResolution(96, 96);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        graphics.Clear(Color.FromArgb(233, 238, 245));
        using var border = new Pen(Color.FromArgb(223, 227, 232));
        using var titleFont = new Font("Microsoft YaHei UI", 28, FontStyle.Bold, GraphicsUnit.Pixel);
        using var commandFont = new Font("Microsoft YaHei UI", 21, FontStyle.Bold, GraphicsUnit.Pixel);
        using var regular = new Font("Microsoft YaHei UI", 17, FontStyle.Regular, GraphicsUnit.Pixel);
        using var text = new SolidBrush(Color.FromArgb(36, 39, 46));
        using var muted = new SolidBrush(Color.FromArgb(102, 112, 133));
        using var header = new SolidBrush(Color.FromArgb(234, 242, 252));
        using var accent = new SolidBrush(Color.FromArgb(76, 120, 168));
        using var alternate = new SolidBrush(Color.FromArgb(250, 252, 255));
        using var card = Rounded(new Rectangle(24, 24, width - 48, height - 48), 10);
        graphics.FillPath(Brushes.White, card);
        graphics.DrawPath(border, card);
        using var title = Rounded(new Rectangle(55, 52, 650, 62), 6);
        graphics.FillPath(header, title);
        graphics.FillRectangle(accent, 55, 58, 4, 50);
        graphics.DrawString(titleText, titleFont, text, 75, 64);
        var y = 137;
        foreach (var row in rows)
        {
            graphics.FillRectangle(row.IsHeading ? header : alternate, 55, y, 650, 80);
            using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            graphics.DrawString(row.Name, commandFont, row.IsHeading ? accent : text, new RectangleF(70, y + (row.IsHeading ? 25 : 8), 620, 30), format);
            if (!row.IsHeading) graphics.DrawString(row.Description, regular, muted, new RectangleF(70, y + 43, 620, 28), format);
            y += 88;
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static GraphicsPath Rounded(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
