using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ChatApp.Ui.ViewModels;

namespace ChatApp.Ui.Views;

public partial class ShellView : UserControl
{
    public ShellView()
    {
        InitializeComponent();
    }

    private ShellViewModel? ViewModel => DataContext as ShellViewModel;

    /// <summary>
    /// Mention autocomplete lives in code-behind because it is caret work: the token
    /// being typed is defined by the caret position, and accepting a candidate splices
    /// text at the caret — neither is expressible through bindings. The view model only
    /// answers "who matches this token".
    /// </summary>
    private void OnComposerTextChanged(object? sender, TextChangedEventArgs e)
        => UpdateMentionQuery();

    private void OnComposerKeyDown(object? sender, KeyEventArgs e)
    {
        var vm = ViewModel;
        if (vm is null || !vm.IsMentionPopupOpen)
            return;

        switch (e.Key)
        {
            case Key.Escape:
                vm.UpdateMentionQuery(null);
                e.Handled = true;
                break;

            // Enter/Tab accept the top candidate — the common case is typing a few
            // characters and confirming, not arrowing through a list.
            case Key.Enter or Key.Tab when vm.MentionCandidates.Count > 0:
                AcceptMention(vm.MentionCandidates[0]);
                e.Handled = true;
                break;
        }
    }

    private void OnMentionCandidateClicked(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is MentionCandidate candidate)
            AcceptMention(candidate);
    }

    private void UpdateMentionQuery()
    {
        var vm = ViewModel;
        var composer = this.FindControl<TextBox>("RoomComposer");
        if (vm is null || composer is null)
            return;

        vm.UpdateMentionQuery(GetMentionToken(composer, out _));
    }

    /// <summary>
    /// The token is the run of word characters between an <c>@</c> and the caret, with
    /// the <c>@</c> either at the start of the text or after whitespace — matching how
    /// the tokens are parsed out again at send time.
    /// </summary>
    private static string? GetMentionToken(TextBox composer, out int tokenStart)
    {
        tokenStart = -1;

        var text = composer.Text ?? string.Empty;
        var caret = Math.Clamp(composer.CaretIndex, 0, text.Length);

        var at = text.LastIndexOf('@', Math.Max(0, caret - 1));
        if (at < 0 || at >= caret)
            return null;

        if (at > 0 && !char.IsWhiteSpace(text[at - 1]))
            return null;

        var token = text[(at + 1)..caret];
        if (token.Length == 0 || !AllWordChars(token))
            return null;

        tokenStart = at;
        return token;
    }

    private static bool AllWordChars(string token)
    {
        foreach (var c in token)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
                return false;
        }

        return true;
    }

    private void AcceptMention(MentionCandidate candidate)
    {
        var vm = ViewModel;
        var composer = this.FindControl<TextBox>("RoomComposer");
        if (vm is null || composer is null)
            return;

        if (GetMentionToken(composer, out var tokenStart) is null || tokenStart < 0)
        {
            vm.UpdateMentionQuery(null);
            return;
        }

        var text = composer.Text ?? string.Empty;
        var caret = Math.Clamp(composer.CaretIndex, 0, text.Length);

        var replacement = $"@{candidate.UserName} ";
        composer.Text = text[..tokenStart] + replacement + text[caret..];
        composer.CaretIndex = tokenStart + replacement.Length;

        vm.UpdateMentionQuery(null);
        composer.Focus();
    }
}
