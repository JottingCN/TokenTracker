using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TokenTracker.Core.Models;

namespace TokenTracker_App.Ui;

internal static class TableRowFactory
{
    private static readonly Brush? DividerBrush = ResolveBrush("DividerStrokeColorDefaultBrush");

    public static FrameworkElement CreateModelRow(GridLength[] widths, ModelRow row) =>
        CreateTextRow(
            widths,
            row.Model,
            row.CallsText,
            row.TokensText,
            row.HitRateText,
            row.CostText);

    public static FrameworkElement CreateSessionRow(
        GridLength[] widths,
        SessionRow row,
        RoutedEventHandler onClick,
        bool includeModel = true)
    {
        var grid = CreateGrid(widths);
        var titleCell = new Grid { VerticalAlignment = VerticalAlignment.Center };
        var link = new HyperlinkButton
        {
            Content = row.SessionDisplay,
            Tag = row.SessionId,
            FontSize = 12,
            Padding = new Thickness(12, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        link.Click += onClick;
        titleCell.Children.Add(link);
        grid.Children.Add(titleCell);

        var column = 1;
        if (includeModel)
        {
            AddText(grid, column++, row.Model);
        }

        AddText(grid, column++, row.CallsText);
        AddText(grid, column++, row.InputText);
        AddText(grid, column++, row.CacheText);
        AddText(grid, column++, row.HitRateText);
        AddText(grid, column++, row.OutputText);
        AddText(grid, column++, row.ReasoningText);
        AddText(grid, column++, row.CostText);
        AddText(grid, column++, row.LastSeenText);
        return Wrap(grid);
    }

    public static FrameworkElement CreateCallRow(
        GridLength[] widths,
        CallRow row,
        bool includeSession = true) =>
        includeSession
            ? CreateTextRow(
                widths,
                row.TimeText,
                row.SessionDisplay,
                row.Model,
                row.InputText,
                row.CacheText,
                row.OutputText,
                row.HitRateText,
                row.CostText,
                row.PeriodText)
            : CreateTextRow(
                widths,
                row.TimeText,
                row.Model,
                row.InputText,
                row.CacheText,
                row.OutputText,
                row.HitRateText,
                row.CostText,
                row.PeriodText);

    public static FrameworkElement CreateEmptyRow() =>
        new TextBlock
        {
            Text = "暂无数据",
            Opacity = 0.6,
            Margin = new Thickness(16, 12, 16, 12),
        };

    private static FrameworkElement CreateTextRow(GridLength[] widths, params string[] values)
    {
        var grid = CreateGrid(widths);
        for (var i = 0; i < values.Length && i < widths.Length; i++)
        {
            AddText(grid, i, values[i]);
        }

        return Wrap(grid);
    }

    private static Grid CreateGrid(GridLength[] widths)
    {
        var grid = new Grid { MinHeight = 30 };
        foreach (var width in widths)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
        }

        return grid;
    }

    private static void AddText(Grid grid, int column, string text)
    {
        var cell = new Grid
        {
            Padding = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        cell.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(cell, column);
        grid.Children.Add(cell);
    }

    private static FrameworkElement Wrap(Grid grid) =>
        new Border
        {
            Child = grid,
            BorderBrush = DividerBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };

    private static Brush? ResolveBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null;
}
