using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BetterTranslator.App.Services;

/// <summary>
/// The one place that decides whether motion runs. Every animation in the
/// application goes through here, so the reduced-motion flag is read once
/// rather than tested at each call site. When
/// <see cref="SystemParameters.ClientAreaAnimation"/> is false nothing
/// animates and the end state is applied immediately.
/// </summary>
public static class MotionService
{
    public static bool Enabled => Override ?? SystemParameters.ClientAreaAnimation;

    /// <summary>
    /// Forces the answer, so the reduced-motion path can be driven without
    /// changing a machine-wide setting. Null hands the question back to Windows.
    /// Scoped to the thread that set it, so one caller cannot decide motion for
    /// another.
    /// </summary>
    public static bool? Override
    {
        get => _override;
        set => _override = value;
    }

    [ThreadStatic]
    private static bool? _override;

    /// <summary>
    /// Animates a double to <paramref name="to"/>. WPF has no cubic-bezier
    /// easing function, so every curve is a KeySpline carried by a single
    /// SplineDoubleKeyFrame.
    /// </summary>
    /// <param name="completed">
    /// Runs once the animation has arrived, and runs immediately when motion is
    /// off. Callers use it to hand a property back to its binding, which an
    /// animation left in place would otherwise keep overriding.
    /// </param>
    public static void AnimateDouble(
        UIElement target,
        DependencyProperty property,
        double to,
        Duration duration,
        KeySpline easing,
        Action? completed = null)
    {
        if (!Enabled)
        {
            Settle(target, property, to);
            completed?.Invoke();
            return;
        }

        var animation = new DoubleAnimationUsingKeyFrames { Duration = duration, FillBehavior = FillBehavior.HoldEnd };
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromPercent(1), easing));

        if (completed is not null)
        {
            animation.Completed += (_, _) => completed();
        }

        target.BeginAnimation(property, animation);
    }

    /// <summary>
    /// Same for a value that lives on a Transform or another Animatable rather
    /// than on the element itself, such as a TranslateTransform offset.
    /// </summary>
    public static void AnimateDouble(
        Animatable target,
        DependencyProperty property,
        double to,
        Duration duration,
        KeySpline easing)
    {
        if (!Enabled)
        {
            target.BeginAnimation(property, null);
            target.SetValue(property, to);
            return;
        }

        var animation = new DoubleAnimationUsingKeyFrames { Duration = duration, FillBehavior = FillBehavior.HoldEnd };
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromPercent(1), easing));
        target.BeginAnimation(property, animation);
    }

    /// <summary>The progress bar width animation is linear and takes no KeySpline.</summary>
    public static void AnimateDoubleLinear(UIElement target, DependencyProperty property, double to, Duration duration)
    {
        if (!Enabled)
        {
            Settle(target, property, to);
            return;
        }

        target.BeginAnimation(property, new DoubleAnimation(to, duration) { FillBehavior = FillBehavior.HoldEnd });
    }

    /// <summary>
    /// The one loop the system allows: indeterminate progress, which has no end
    /// state to settle on because it is not a transition. Linear, because a
    /// sweep that eased would read as a pulse and easing an indeterminate loop
    /// states a progress it does not know.
    /// </summary>
    /// <returns>
    /// True when the loop is running. False means motion is off, and the caller
    /// has to show its static fallback rather than a loop nobody will see -- the
    /// only thing on this surface with no end state for <see cref="Settle"/> to
    /// apply, which is why this one reports back and the others do not.
    /// </returns>
    public static bool BeginLoop(
        Animatable target,
        DependencyProperty property,
        double from,
        double to,
        Duration period)
    {
        if (!Enabled)
        {
            target.BeginAnimation(property, null);
            return false;
        }

        target.BeginAnimation(
            property,
            new DoubleAnimation(from, to, period) { RepeatBehavior = RepeatBehavior.Forever });

        return true;
    }

    /// <summary>
    /// Stops a loop and hands the property back. Called whenever the loop is not
    /// being watched -- the region is hidden, the window is not in front -- so an
    /// infinite animation cannot go on costing frames off screen.
    /// </summary>
    public static void EndLoop(Animatable target, DependencyProperty property, double park)
    {
        target.BeginAnimation(property, null);
        target.SetValue(property, park);
    }

    public static void AnimateColor(
        Animatable target,
        DependencyProperty property,
        Color to,
        Duration duration,
        KeySpline easing)
    {
        if (!Enabled)
        {
            target.BeginAnimation(property, null);
            target.SetValue(property, to);
            return;
        }

        var animation = new ColorAnimationUsingKeyFrames { Duration = duration, FillBehavior = FillBehavior.HoldEnd };
        animation.KeyFrames.Add(new SplineColorKeyFrame(to, KeyTime.FromPercent(1), easing));
        target.BeginAnimation(property, animation);
    }

    /// <summary>
    /// Clearing the animation before setting the value matters: an animation
    /// left in place keeps overriding the local value it was meant to reach.
    /// </summary>
    private static void Settle(UIElement target, DependencyProperty property, double to)
    {
        target.BeginAnimation(property, null);
        target.SetValue(property, to);
    }
}
