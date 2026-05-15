using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace LiveDialogueTranslator.App;

public static class TextBoxAutoScrollBehavior
{
    private static readonly DependencyProperty LastLineCountProperty =
        DependencyProperty.RegisterAttached(
            "LastLineCount",
            typeof(int),
            typeof(TextBoxAutoScrollBehavior),
            new PropertyMetadata(0));

    public static readonly DependencyProperty ScrollToEndOnTextChangeProperty =
        DependencyProperty.RegisterAttached(
            "ScrollToEndOnTextChange",
            typeof(bool),
            typeof(TextBoxAutoScrollBehavior),
            new PropertyMetadata(false, OnScrollToEndOnTextChangeChanged));

    public static void SetScrollToEndOnTextChange(TextBox textBox, bool value)
    {
        textBox.SetValue(ScrollToEndOnTextChangeProperty, value);
    }

    public static bool GetScrollToEndOnTextChange(TextBox textBox)
    {
        return (bool)textBox.GetValue(ScrollToEndOnTextChangeProperty);
    }

    private static void OnScrollToEndOnTextChangeChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not TextBox textBox)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            textBox.TextChanged += TextBox_TextChanged;
            textBox.SizeChanged += TextBox_SizeChanged;
            textBox.Loaded += TextBox_Loaded;
            QueueScrollToEnd(textBox, animateWhenLineAdded: false);
        }
        else
        {
            textBox.TextChanged -= TextBox_TextChanged;
            textBox.SizeChanged -= TextBox_SizeChanged;
            textBox.Loaded -= TextBox_Loaded;
        }
    }

    private static void TextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        QueueScrollToEnd((TextBox)sender, animateWhenLineAdded: true);
    }

    private static void TextBox_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        QueueScrollToEnd((TextBox)sender, animateWhenLineAdded: false);
    }

    private static void TextBox_Loaded(object sender, RoutedEventArgs e)
    {
        QueueScrollToEnd((TextBox)sender, animateWhenLineAdded: false);
    }

    private static void QueueScrollToEnd(TextBox textBox, bool animateWhenLineAdded)
    {
        textBox.Dispatcher.BeginInvoke(
            new Action(() =>
            {
                var previousLineCount = GetLastLineCount(textBox);
                textBox.CaretIndex = textBox.Text.Length;
                textBox.ScrollToEnd();
                var currentLineCount = Math.Max(1, textBox.LineCount);
                SetLastLineCount(textBox, currentLineCount);
                if (animateWhenLineAdded && previousLineCount > 0 && currentLineCount > previousLineCount)
                {
                    AnimateSlide(textBox);
                }
            }),
            DispatcherPriority.ContextIdle);
    }

    private static void AnimateSlide(TextBox textBox)
    {
        if (string.IsNullOrEmpty(textBox.Text))
        {
            return;
        }

        if (textBox.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform();
            textBox.RenderTransform = transform;
        }

        transform.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation
            {
                From = 12,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(360),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            },
            HandoffBehavior.SnapshotAndReplace);
    }

    private static void SetLastLineCount(TextBox textBox, int lineCount)
    {
        textBox.SetValue(LastLineCountProperty, lineCount);
    }

    private static int GetLastLineCount(TextBox textBox)
    {
        return (int)textBox.GetValue(LastLineCountProperty);
    }
}
