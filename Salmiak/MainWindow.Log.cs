using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace Salmiak;

public partial class MainWindow
{
    //The status bar is one line, so messages sent back to back overwrite each other and the ones
    //nobody read in time are lost. Say() is the single way of telling the user anything: it still
    //paints the status bar with the newest message, and it also appends it to this log. The log is
    //collected whether or not the panel is open, so opening it late still shows what just happened.
    //Newest first: the messages that matter now are always on screen without scrolling. A run of
    //the same message in a row becomes one line with a ×count, so a long burst of one kind of
    //feedback cannot crowd the tail out.
    private sealed record LogLine(string Time, string Text, int Times);
    private const int LogKeep = 400;
    private readonly List<LogLine> _log = new();
    private bool _logVisible;

    private void Say(string msg)
    {
        StatusText.Text = msg;
        var time = $"{System.DateTime.Now:HH:mm:ss}";
        if (_log.Count > 0 && _log[^1].Text == msg)
            _log[^1] = new LogLine(time, msg, _log[^1].Times + 1);
        else
        {
            _log.Add(new LogLine(time, msg, 1));
            if (_log.Count > LogKeep) _log.RemoveAt(0);
        }
        RefreshLogPanel();
    }

    //Adds to the newest message instead of starting a new one, for status lines built in pieces.
    private void SayMore(string tail)
    {
        StatusText.Text = $"{StatusText.Text}{tail}";
        if (_log.Count > 0)
        {
            var last = _log[^1];
            _log[^1] = new LogLine(last.Time, $"{last.Text}{tail}", last.Times);
        }
        RefreshLogPanel();
    }

    //Status readouts that change every few seconds while the camera moves (how many tiles are
    //streamed in) belong to the status bar only: logging them would bury every real message.
    private void Status(string msg) => StatusText.Text = msg;

    private void RefreshLogPanel()
    {
        if (!_logVisible) return;
        var sb = new System.Text.StringBuilder();
        for (int i = _log.Count - 1; i >= 0; i--)
        {
            var ln = _log[i];
            sb.Append(ln.Times > 1 ? $"{ln.Time}  {ln.Text}   ×{ln.Times}\n" : $"{ln.Time}  {ln.Text}\n");
        }
        LogText.Text = sb.ToString();
        LogHeader.Text = _log.Count == 0 ? "nothing logged yet"
            : $"{_log.Count} message(s)   newest first   ·   scroll down for older   ·   ×N = sent N times";
    }

    //Hidden by default. The Log button in the status bar opens the panel over the viewport corner,
    //and clicking the same button again closes it; closing hides the panel, it does not erase the
    //log, so the next opening still shows the whole tail.
    private void ToggleLog_Click(object sender, RoutedEventArgs e) => SetLogPanel(!_logVisible);

    private void SetLogPanel(bool open)
    {
        _logVisible = open;
        LogPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        RefreshLogPanel();
    }

    //Empties the log: useful after a long run, so the next bake or export starts a readable tail.
    private void LogClear_Click(object sender, RoutedEventArgs e)
    {
        _log.Clear();
        RefreshLogPanel();
    }
}
