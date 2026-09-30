using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;

namespace CarriolaConverter;

public sealed partial class MainWindow
{
    // Esse intervalo se sobrepõe ao carregamento real dos formatos e ajustes.
    private static readonly TimeSpan StartupMinimumDuration = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan StartupFadeDuration = TimeSpan.FromMilliseconds(320);
    private readonly Stopwatch _startupClock = new();
    private readonly CancellationTokenSource _startupCancellation = new();
    private Storyboard? _startupEntrance;
    private Storyboard? _startupExit;

    private void BeginStartup()
    {
        _startupClock.Restart();
        try
        {
            bool animate = _uiSettings.AnimationsEnabled;
            StartupProgress.IsActive = animate;
            if (!animate)
            {
                MainContent.ChildrenTransitions = null;
                return;
            }

            _startupEntrance = new Storyboard();
            var duration = TimeSpan.FromMilliseconds(380);
            AddStartupAnimation(_startupEntrance, StartupBrand, "Opacity", 0, 1, duration);
            AddStartupAnimation(_startupEntrance, StartupBrandTransform, "ScaleX", 0.96, 1, duration);
            AddStartupAnimation(_startupEntrance, StartupBrandTransform, "ScaleY", 0.96, 1, duration);
            AddStartupAnimation(_startupEntrance, StartupBrandTransform, "TranslateY", 8, 0, duration);
            _startupEntrance.Begin();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Animação de abertura: {ex.Message}");
            StopStartupAnimations();
        }
    }

    private async Task CompleteStartupAsync()
    {
        try
        {
            if (_closed || _closeRequested) return;

            bool animate = _uiSettings.AnimationsEnabled;
            var remaining = StartupMinimumDuration - _startupClock.Elapsed;
            if (animate && remaining > TimeSpan.Zero)
                await Task.Delay(remaining, _startupCancellation.Token);

            if (_closed || _closeRequested) return;

            StopStartupAnimations();
            StartupProgress.IsActive = false;
            MainContent.Visibility = Visibility.Visible;

            if (!animate)
            {
                MainContent.ChildrenTransitions = null;
                return;
            }

            _startupExit = new Storyboard();
            AddStartupAnimation(_startupExit, StartupOverlay, "Opacity", 1, 0, StartupFadeDuration);
            AddStartupAnimation(_startupExit, MainContent, "Opacity", 0, 1, StartupFadeDuration);
            _startupExit.Begin();

            // A espera é assíncrona e cancelável. Não depende de Completed,
            // que pode não disparar quando a janela é fechada durante a animação.
            await Task.Delay(StartupFadeDuration, _startupCancellation.Token);
        }
        catch (OperationCanceledException) when (_startupCancellation.IsCancellationRequested)
        {
            // O fechamento segue pelo fluxo normal de limpeza da MainWindow.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Transição de abertura: {ex.Message}");
        }
        finally
        {
            _startupClock.Stop();
            StopStartupAnimations();
            if (!_closed && !_closeRequested)
            {
                StartupProgress.IsActive = false;
                MainContent.Opacity = 1;
                MainContent.Visibility = Visibility.Visible;
                StartupOverlay.Visibility = Visibility.Collapsed;
                // Libera a imagem de abertura após a transição.
                StartupLogo.Source = null;
            }
        }
    }

    private void CancelStartup()
    {
        if (!_startupCancellation.IsCancellationRequested) _startupCancellation.Cancel();
        StopStartupAnimations();
        if (!_closed)
        {
            StartupProgress.IsActive = false;
            StartupStatus.Text = "Fechando...";
        }
    }

    private void StopStartupAnimations()
    {
        try { _startupEntrance?.Stop(); }
        catch (Exception ex) { Debug.WriteLine(ex.Message); }
        try { _startupExit?.Stop(); }
        catch (Exception ex) { Debug.WriteLine(ex.Message); }
        _startupEntrance = null;
        _startupExit = null;
    }

    private static void AddStartupAnimation(Storyboard storyboard, DependencyObject target,
        string property, double from, double to, TimeSpan duration)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(duration),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }
}
