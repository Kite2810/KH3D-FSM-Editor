using System.Collections.Generic;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;

namespace FsmTool;

// IsAttack -> Visibility (used by the ATK badge column)
public sealed class BoolVis : IValueConverter
{
    public static readonly BoolVis Instance = new();
    public object Convert(object value, Type t, object p, CultureInfo c)
        => (value is bool b && b) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => throw new NotSupportedException();
}

public partial class MainWindow : Window
{
    private FsmDocument? _doc;
    private bool _filterAttack;
    private int? _selOffset;          // EntryOffset of selected transition, to restore across reparse

    public MainWindow() => InitializeComponent();

    // ════════ window chrome ════════
    private void MinimizeBtn_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeBtn_Click(object s, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        MaxIcon.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }
    private void CloseBtn_Click(object s, RoutedEventArgs e) => Close();

    // ════════ load ════════
    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "FSM files (*.fsm)|*.fsm|All files (*.*)|*.*",
            Title = "Open an FSM file"
        };
        if (dlg.ShowDialog() == true) Load(dlg.FileName);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            Load(files[0]);
    }

    private void Load(string path)
    {
        try
        {
            _doc = Fsm.Parse(path);
            _selOffset = null;
            DataContext = _doc;
            EmptyState.Visibility = Visibility.Collapsed;
            MainContent.Visibility = Visibility.Visible;
            BtnRevert.IsEnabled = BtnSaveAs.IsEnabled = BtnRedirect.IsEnabled = BtnReport.IsEnabled = true;
            UpdateInfo();
            RebindGrid();
            Status.Text = $"Loaded {Path.GetFileName(path)} — {_doc.Graph.Count} states, " +
                          $"{_doc.AttackEdges.Count} attack edge(s).";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not parse file",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            Status.Text = "Parse failed: " + ex.Message;
        }
    }

    // Re-read patched bytes; rebuild list and restore selection. Deferred so we
    // never tear down the control that raised the event.
    private void Refresh(string status)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_doc is null) return;
            try
            {
                _doc = Fsm.Parse(_doc.Bytes, _doc.Path);
                DataContext = _doc;
                UpdateInfo();
                RebindGrid();          // re-selects by _selOffset -> repopulates details
                Status.Text = status;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Edit failed",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }));
    }

    private void UpdateInfo()
    {
        if (_doc is null) return;
        TitleFile.Text = "— " + Path.GetFileName(_doc.Path);
        InfoName.Text = Path.GetFileName(_doc.Path);
        InfoMeta.Text = $"@FSM v{_doc.Version}  ·  {_doc.ByteLength:N0} bytes";
        InfoStates.Text = _doc.Graph.Count.ToString();
        InfoTrans.Text = _doc.AllTransitions.Count.ToString();
        InfoAttacks.Text = _doc.AttackEdges.Count.ToString();
        EditedBadge.Visibility = _doc.HasBackup ? Visibility.Visible : Visibility.Collapsed;
        StatusRight.Text = $"{_doc.Graph.Count} states · {_doc.AttackEdges.Count} attacks";
    }

    // ════════ list ════════
    private void RebindGrid()
    {
        if (_doc is null) return;
        IEnumerable<Transition> src = _doc.AllTransitions;
        if (_filterAttack) src = src.Where(t => t.IsAttack);
        string q = SearchBox.Text?.Trim() ?? "";
        if (q.Length > 0)
            src = src.Where(t => ($"{t.Owner} {t.Target} {t.When}")
                .Contains(q, StringComparison.OrdinalIgnoreCase));
        var list = src.ToList();
        Grid.ItemsSource = list;
        CountBadge.Text = list.Count.ToString();

        if (_selOffset is int off)
        {
            var match = list.FirstOrDefault(t => t.EntryOffset == off);
            if (match != null) { Grid.SelectedItem = match; Grid.ScrollIntoView(match); }
            else ShowNoSelection();
        }
    }

    private void Grid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Grid.SelectedItem is not Transition t) { ShowNoSelection(); return; }
        _selOffset = t.EntryOffset;
        SelPanel.DataContext = t;
        NoCondText.Visibility = t.Conditions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoSelPanel.Visibility = Visibility.Collapsed;
        SelPanel.Visibility = Visibility.Visible;
    }

    private void ShowNoSelection()
    {
        SelPanel.Visibility = Visibility.Collapsed;
        NoSelPanel.Visibility = Visibility.Visible;
    }

    private void FilterAll_Click(object sender, RoutedEventArgs e)
    {
        _filterAttack = false;
        FilterAllBtn.Style = (Style)FindResource("FilterOn");
        FilterAtkBtn.Style = (Style)FindResource("FilterOff");
        RebindGrid();
    }

    private void FilterAttack_Click(object sender, RoutedEventArgs e)
    {
        _filterAttack = true;
        FilterAtkBtn.Style = (Style)FindResource("FilterOn");
        FilterAllBtn.Style = (Style)FindResource("FilterOff");
        RebindGrid();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_doc != null) RebindGrid();
    }

    // ════════ edits ════════
    private void Target_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_doc is null) return;
        if (sender is not ComboBox cb || cb.SelectedItem is not string state) return;
        if (cb.DataContext is not Transition t) return;
        if (state == t.Target) return;
        if (_doc.Retarget(t, state))
            Refresh($"{t.Owner}: target → {state}  (saved; Revert to undo)");
    }

    private void CondBool_Click(object sender, RoutedEventArgs e)
    {
        if (_doc is null) return;
        if (sender is not CheckBox chk || chk.DataContext is not Condition c) return;
        _doc.SetConditionBool(c, chk.IsChecked == true);
        Refresh($"{c.Name} = {(chk.IsChecked == true ? "true" : "false")}  (saved)");
    }

    // change which variable a condition tests
    private void CondName_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_doc is null) return;
        if (sender is not ComboBox cb || cb.SelectedItem is not string name) return;
        if (cb.DataContext is not Condition c) return;
        if (name == c.Name) return;
        if (_doc.SetConditionName(c, name))
            Refresh($"condition → {name}  (saved; Revert to undo)");
    }

    // change a condition's comparison operator
    private void CondOp_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_doc is null) return;
        if (sender is not ComboBox cb || cb.SelectedItem is not string glyph) return;
        if (cb.DataContext is not Condition c) return;
        if (glyph == c.OpText) return;
        if (_doc.SetConditionOpText(c, glyph))
            Refresh($"{c.Name} operator → {glyph}  (saved)");
    }

    private void CondNum_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox tb) CommitNum(tb);
    }

    private void CondNum_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) CommitNum(tb);
    }

    private void CommitNum(TextBox tb)
    {
        if (_doc is null) return;
        if (tb.DataContext is not Condition c) return;
        string txt = tb.Text.Trim();

        if (c.Kind == "int")
        {
            if (!uint.TryParse(txt, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint iv))
            { Refresh("Not a whole number — change ignored."); return; }
            if (iv == c.Raw) return;
            _doc.SetConditionInt(c, iv);
            Refresh($"{c.Name} = {iv}  (saved)");
        }
        else
        {
            if (!float.TryParse(txt, NumberStyles.Float, CultureInfo.InvariantCulture, out float fv))
            { Refresh("Not a number — change ignored."); return; }
            if (fv == c.FloatValue) return;
            _doc.SetConditionFloat(c, fv);
            Refresh($"{c.Name} = {fv.ToString("0.###", CultureInfo.InvariantCulture)}  (saved)");
        }
    }

    private void RedirectOne_Click(object sender, RoutedEventArgs e)
    {
        if (_doc is null) return;
        if (SelPanel.DataContext is not Transition t) return;
        if (t.Target == "Idle") { Status.Text = "Already targets Idle."; return; }
        _doc.RedirectToIdle(t);
        Refresh($"{t.Owner}: target → Idle  (saved; Revert to undo)");
    }

    // ════════ toolbar ════════
    private void Revert_Click(object sender, RoutedEventArgs e)
    {
        if (_doc is null) return;
        if (!_doc.HasBackup) { Status.Text = "Nothing to revert — no edits made yet."; return; }
        var ok = MessageBox.Show(this,
            "Restore the original file? This discards every change you've made.",
            "Revert to original", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ok != MessageBoxResult.OK) return;
        if (_doc.RevertToBackup()) Refresh("Reverted to the original file.");
    }

    private void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (_doc is null) return;
        var dlg = new SaveFileDialog
        {
            Filter = "FSM files (*.fsm)|*.fsm",
            FileName = Path.GetFileNameWithoutExtension(_doc.Path) + "_copy.fsm",
            Title = "Save a copy (original is untouched)"
        };
        if (dlg.ShowDialog() == true)
        {
            try { _doc.SaveAsCopy(dlg.FileName); Status.Text = "Saved copy to " + dlg.FileName; }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Save failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void RedirectAll_Click(object sender, RoutedEventArgs e)
    {
        if (_doc is null) return;
        var ok = MessageBox.Show(this,
            "Redirect EVERY non-Idle transition to Idle?\n\n" +
            "Pins the entity in Idle (pure follow) and removes all native behaviour. " +
            "A one-time .bak is written; use Revert to undo.",
            "Redirect all", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ok != MessageBoxResult.OK) return;
        int n = _doc.RedirectAllToIdle();
        Refresh($"Redirected {n} edge(s) to Idle.");
    }

    private void SaveReport_Click(object sender, RoutedEventArgs e)
    {
        if (_doc is null) return;
        var dlg = new SaveFileDialog
        {
            Filter = "Text file (*.txt)|*.txt",
            FileName = Path.GetFileNameWithoutExtension(_doc.Path) + "_fsm.txt"
        };
        if (dlg.ShowDialog() == true)
        {
            File.WriteAllText(dlg.FileName, _doc.BuildReport());
            Status.Text = "Report written to " + dlg.FileName;
        }
    }
}