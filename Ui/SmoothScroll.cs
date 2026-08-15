using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SoundFluent.Ui;

/// <summary>
/// Smooths wheel input into one continuous pixel-scrolling motion. Further
/// wheel events move the destination without restarting the animation.
/// </summary>
public static class SmoothScroll
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(SmoothScroll),
            new PropertyMetadata(false, OnIsEnabledChanged));

    private static readonly Dictionary<ScrollViewer, ScrollState> States = new();

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    /// <summary>Queues a signed pixel distance on the same glide used by the wheel.</summary>
    public static bool ScrollBy(ScrollViewer viewer, double distance)
    {
        if (viewer.ScrollableHeight <= 0)
            return false;

        GetState(viewer).AddOffset(distance);
        return true;
    }

    private static void OnIsEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not ScrollViewer viewer)
            return;

        if ((bool)e.NewValue)
        {
            viewer.PanningMode = PanningMode.VerticalOnly;
            viewer.PreviewMouseWheel += OnPreviewMouseWheel;
            viewer.Unloaded += OnViewerUnloaded;
        }
        else
        {
            Detach(viewer);
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var viewer = (ScrollViewer)sender;
        if (viewer.ScrollableHeight <= 0)
            return;

        GetState(viewer).AddOffset(-(e.Delta * 0.38));
        e.Handled = true;
    }

    private static ScrollState GetState(ScrollViewer viewer)
    {
        if (States.TryGetValue(viewer, out ScrollState? state))
            return state;

        state = new ScrollState(viewer);
        States.Add(viewer, state);
        return state;
    }

    private static void OnViewerUnloaded(object sender, RoutedEventArgs e) =>
        Detach((ScrollViewer)sender);

    private static void Detach(ScrollViewer viewer)
    {
        viewer.PreviewMouseWheel -= OnPreviewMouseWheel;
        viewer.Unloaded -= OnViewerUnloaded;

        if (States.Remove(viewer, out ScrollState? state))
            state.Stop();
    }

    private sealed class ScrollState
    {
        private const double ResponseSeconds = 0.11;

        private readonly ScrollViewer _viewer;
        private double _target;
        private long _lastFrame;
        private bool _isRunning;

        public ScrollState(ScrollViewer viewer)
        {
            _viewer = viewer;
            _target = viewer.VerticalOffset;
        }

        public void AddOffset(double distance)
        {
            if (!_isRunning)
            {
                // Thumb drags and programmatic scrolling stay authoritative
                // whenever the wheel animation is idle.
                _target = _viewer.VerticalOffset;
                _lastFrame = Stopwatch.GetTimestamp();
                CompositionTarget.Rendering += OnRendering;
                _isRunning = true;
            }

            _target = Math.Clamp(
                _target + distance,
                0,
                _viewer.ScrollableHeight);
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            long now = Stopwatch.GetTimestamp();
            double elapsed = Math.Min(
                (now - _lastFrame) / (double)Stopwatch.Frequency,
                0.05);
            _lastFrame = now;

            _target = Math.Clamp(_target, 0, _viewer.ScrollableHeight);
            double current = _viewer.VerticalOffset;
            double remaining = _target - current;

            if (Math.Abs(remaining) < 0.15)
            {
                _viewer.ScrollToVerticalOffset(_target);
                Stop();
                return;
            }

            // Frame-rate-independent exponential response. Position is never
            // reset, so fast wheel sequences remain one uninterrupted glide.
            double blend = 1 - Math.Exp(-elapsed / ResponseSeconds);
            _viewer.ScrollToVerticalOffset(current + (remaining * blend));
        }

        public void Stop()
        {
            if (!_isRunning)
                return;

            CompositionTarget.Rendering -= OnRendering;
            _isRunning = false;
        }
    }
}
