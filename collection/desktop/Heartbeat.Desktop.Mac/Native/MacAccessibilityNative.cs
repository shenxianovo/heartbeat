using System.Runtime.InteropServices;
using Heartbeat.Collector.System.Observations;
using Heartbeat.Desktop.Mac.Observations;
using Serilog;

namespace Heartbeat.Desktop.Mac.Native;

/// <summary>
/// macOS AXUIElement/AXObserver 窄 adapter。专用 CFRunLoop 只观察当前应用的 focused-window
/// 与当前窗口标题；权限请求由上层显式用户动作触发。
/// </summary>
public sealed class MacAccessibilityNative : IMacAccessibilityNative, IDisposable
{
    private static readonly nint FocusedWindowAttribute = CoreFoundation.CreateString("AXFocusedWindow");
    private static readonly nint TitleAttribute = CoreFoundation.CreateString("AXTitle");
    private static readonly nint FocusedWindowChangedNotification =
        CoreFoundation.CreateString("AXFocusedWindowChanged");
    private static readonly nint TitleChangedNotification = CoreFoundation.CreateString("AXTitleChanged");
    private static readonly nint DefaultRunLoopMode = CoreFoundation.CreateString("kCFRunLoopDefaultMode");
    private static readonly AxObserverCallback ObserverCallback = Session.HandleNotification;
    private static readonly Dictionary<nint, WeakReference<Session>> Instances = [];
    private static readonly object InstancesGate = new();

    private readonly object _gate = new();
    private Session? _session;
    private bool _disposed;

    public MacAccessibilityNative()
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("macOS Accessibility observation requires macOS.");
    }

    public event Action<MacAccessibilityObservation>? Observation;
    public event Action<Exception>? Failed;

    public bool IsAvailable => OperatingSystem.IsMacOS();
    public bool IsProcessTrusted => Native.AXIsProcessTrusted();

    public void RequestProcessTrust()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var dictionary = nint.Zero;
        try
        {
            var keys = new[] { CoreFoundation.AccessibilityPromptKey };
            var values = new[] { CoreFoundation.TrueValue };
            dictionary = CoreFoundation.CreateDictionary(keys, values);
            _ = Native.AXIsProcessTrustedWithOptions(dictionary);
        }
        finally
        {
            if (dictionary != 0) CoreFoundation.Release(dictionary);
        }
    }

    public string? ReadFocusedWindowTitle(int processIdentifier)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (processIdentifier <= 0 || !IsProcessTrusted) return null;

        var application = Native.AXUIElementCreateApplication(processIdentifier);
        if (application == 0) return null;
        var window = nint.Zero;
        try
        {
            return Session.ReadFocusedWindowTitle(application, out window);
        }
        finally
        {
            if (window != 0) CoreFoundation.Release(window);
            CoreFoundation.Release(application);
        }
    }

    public void ObserveApplication(int processIdentifier)
    {
        if (processIdentifier <= 0) return;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session is { } current && !current.Thread.Completion.IsCompleted)
            {
                if (current.Thread.IsStopping)
                    throw new InvalidOperationException("The previous Accessibility session is still stopping.");
                current.DesiredProcessIdentifier = processIdentifier;
                return;
            }
            _session = new Session(this) { DesiredProcessIdentifier = processIdentifier };
            _session.Thread.Start();
        }
    }

    public void StopObserving()
    {
        Session? session;
        lock (_gate) session = _session;
        session?.Thread.RequestStop();
        session?.Thread.Completion.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        lock (_gate)
            if (ReferenceEquals(_session, session) && session?.Thread.Completion.IsCompleted == true)
                _session = null;
    }

    private void Publish(Session session, MacAccessibilityObservation observation)
    {
        lock (_gate)
            if (!ReferenceEquals(_session, session) || session.Thread.IsStopping) return;
        Observation?.Invoke(observation);
    }

    private void PublishFailure(Session session, Exception error)
    {
        lock (_gate)
            if (!ReferenceEquals(_session, session)) return;
        Failed?.Invoke(error);
    }

    private sealed class Session
    {
        private readonly MacAccessibilityNative _owner;
        private nint _observer;
        private nint _applicationElement;
        private nint _focusedWindowElement;
        private nint _runLoop;
        private int _observedProcessIdentifier;
        private int _desiredProcessIdentifier;
        public int DesiredProcessIdentifier
        {
            get => Volatile.Read(ref _desiredProcessIdentifier);
            set => Volatile.Write(ref _desiredProcessIdentifier, value);
        }
        public ObservationThread Thread { get; }
        public Session(MacAccessibilityNative owner)
        {
            _owner = owner;
            Thread = new ObservationThread("Heartbeat macOS Accessibility", RunObserverLoop, error => owner.PublishFailure(this, error));
        }

        private void RunObserverLoop(CancellationToken cancellationToken)
        {
            var attachedProcessIdentifier = 0;
            _runLoop = Native.CFRunLoopGetCurrent();
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var desired = DesiredProcessIdentifier;

                    if (desired != attachedProcessIdentifier)
                    {
                        DetachObserver();
                        attachedProcessIdentifier = 0;
                        if (desired > 0 && Native.AXIsProcessTrusted() && AttachObserver(desired))
                            attachedProcessIdentifier = desired;
                    }

                    _ = Native.CFRunLoopRunInMode(DefaultRunLoopMode, 0.2, true);
                }
            }
            catch (Exception exception)
            {
                Log.Warning(exception, "macOS Accessibility observer 异常停止");
                throw;
            }
            finally
            {
                DetachObserver();
                _runLoop = 0;
            }
        }

        private bool AttachObserver(int processIdentifier)
        {
            _applicationElement = Native.AXUIElementCreateApplication(processIdentifier);
            if (_applicationElement == 0) throw new InvalidOperationException("AX application creation failed.");

            var error = Native.AXObserverCreate(processIdentifier, ObserverCallback, out _observer);
            if (error != 0 || _observer == 0)
            {
                DetachObserver();
                throw new InvalidOperationException($"AX observer creation failed: {error}.");
            }

            lock (InstancesGate)
                Instances[_observer] = new WeakReference<Session>(this);

            var source = Native.AXObserverGetRunLoopSource(_observer);
            Native.CFRunLoopAddSource(_runLoop, source, DefaultRunLoopMode);
            RegisterNotification(
                _observer,
                _applicationElement,
                FocusedWindowChangedNotification,
                0);
            ReplaceFocusedWindow(emitObservation: false);
            _observedProcessIdentifier = processIdentifier;
            return true;
        }

        private void DetachObserver()
        {
            if (_observer != 0)
            {
                if (_focusedWindowElement != 0)
                    _ = Native.AXObserverRemoveNotification(
                        _observer,
                        _focusedWindowElement,
                        TitleChangedNotification);
                if (_applicationElement != 0)
                    _ = Native.AXObserverRemoveNotification(
                        _observer,
                        _applicationElement,
                        FocusedWindowChangedNotification);

                var source = Native.AXObserverGetRunLoopSource(_observer);
                if (_runLoop != 0 && source != 0)
                    Native.CFRunLoopRemoveSource(_runLoop, source, DefaultRunLoopMode);
                lock (InstancesGate)
                    Instances.Remove(_observer);
            }

            if (_focusedWindowElement != 0) CoreFoundation.Release(_focusedWindowElement);
            if (_observer != 0) CoreFoundation.Release(_observer);
            if (_applicationElement != 0) CoreFoundation.Release(_applicationElement);
            _focusedWindowElement = 0;
            _observer = 0;
            _applicationElement = 0;
            _observedProcessIdentifier = 0;
        }

        private static void RegisterNotification(nint observer, nint element, nint notification, nint context)
        {
            var error = Native.AXObserverAddNotification(observer, element, notification, context);
            if (error != 0) throw new InvalidOperationException($"AX notification registration failed: {error}.");
        }

        private void ReplaceFocusedWindow(bool emitObservation)
        {
            if (_observer == 0 || _applicationElement == 0) return;

            if (_focusedWindowElement != 0)
            {
                _ = Native.AXObserverRemoveNotification(
                    _observer,
                    _focusedWindowElement,
                    TitleChangedNotification);
                CoreFoundation.Release(_focusedWindowElement);
                _focusedWindowElement = 0;
            }

            var title = ReadFocusedWindowTitle(_applicationElement, out var focusedWindow);
            _focusedWindowElement = focusedWindow;
            if (_focusedWindowElement != 0)
                RegisterNotification(
                    _observer,
                    _focusedWindowElement,
                    TitleChangedNotification,
                    0);

            if (emitObservation)
                _owner.Publish(this, new MacAccessibilityObservation(
                    MacAccessibilityObservationKind.FocusedWindowChanged,
                    title,
                    _observedProcessIdentifier));
        }

        internal static string? ReadFocusedWindowTitle(nint application, out nint focusedWindow)
        {
            focusedWindow = 0;
            var error = Native.AXUIElementCopyAttributeValue(
                application,
                FocusedWindowAttribute,
                out focusedWindow);
            if (error != 0 || focusedWindow == 0)
                return null;

            var titleValue = nint.Zero;
            try
            {
                error = Native.AXUIElementCopyAttributeValue(focusedWindow, TitleAttribute, out titleValue);
                return error == 0 && titleValue != 0
                    ? CoreFoundation.ReadString(titleValue)
                    : null;
            }
            finally
            {
                if (titleValue != 0) CoreFoundation.Release(titleValue);
            }
        }

        internal static void HandleNotification(
            nint observer,
            nint element,
            nint notification,
            nint refcon)
        {
            Session? instance = null;
            lock (InstancesGate)
            {
                if (Instances.TryGetValue(observer, out var weak))
                    weak.TryGetTarget(out instance);
            }
            if (instance == null || instance.Thread.IsStopping) return;

            try
            {
                var name = CoreFoundation.ReadString(notification);
                if (name == "AXFocusedWindowChanged")
                {
                    instance.ReplaceFocusedWindow(emitObservation: true);
                }
                else if (name == "AXTitleChanged")
                {
                    var title = instance._focusedWindowElement == 0
                        ? null
                        : ReadTitle(instance._focusedWindowElement);
                    instance._owner.Publish(instance, new MacAccessibilityObservation(
                        MacAccessibilityObservationKind.TitleChanged,
                        title,
                        instance._observedProcessIdentifier));
                }
            }
            catch (Exception error) { instance.Thread.Fail(error); }
        }

        private static string? ReadTitle(nint element)
        {
            var titleValue = nint.Zero;
            try
            {
                var error = Native.AXUIElementCopyAttributeValue(element, TitleAttribute, out titleValue);
                return error == 0 && titleValue != 0
                    ? CoreFoundation.ReadString(titleValue)
                    : null;
            }
            finally
            {
                if (titleValue != 0) CoreFoundation.Release(titleValue);
            }
        }

    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        StopObserving();
        GC.SuppressFinalize(this);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AxObserverCallback(
        nint observer,
        nint element,
        nint notification,
        nint refcon);

    private static class CoreFoundation
    {
        private const uint Utf8Encoding = 0x08000100;
        private static readonly nint CoreFoundationHandle = NativeLibrary.Load(
            "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation");
        private static readonly nint ApplicationServicesHandle = NativeLibrary.Load(
            "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices");

        public static nint AccessibilityPromptKey
        {
            get
            {
                var symbol = NativeLibrary.GetExport(
                    ApplicationServicesHandle,
                    "kAXTrustedCheckOptionPrompt");
                return Marshal.ReadIntPtr(symbol);
            }
        }

        public static nint TrueValue
        {
            get
            {
                var symbol = NativeLibrary.GetExport(CoreFoundationHandle, "kCFBooleanTrue");
                return Marshal.ReadIntPtr(symbol);
            }
        }

        public static nint CreateString(string value) =>
            Native.CFStringCreateWithCString(0, value, Utf8Encoding);

        public static nint CreateDictionary(nint[] keys, nint[] values) =>
            Native.CFDictionaryCreate(0, keys, values, keys.Length, 0, 0);

        public static string? ReadString(nint value)
        {
            if (value == 0) return null;
            var length = Native.CFStringGetLength(value);
            var capacity = Native.CFStringGetMaximumSizeForEncoding(length, Utf8Encoding) + 1;
            if (capacity <= 1) return string.Empty;
            var buffer = new byte[capacity];
            return Native.CFStringGetCString(value, buffer, buffer.Length, Utf8Encoding)
                ? System.Text.Encoding.UTF8.GetString(buffer, 0, Array.IndexOf(buffer, (byte)0))
                : null;
        }

        public static void Release(nint value)
        {
            if (value != 0) Native.CFRelease(value);
        }
    }

    private static class Native
    {
        private const string ApplicationServices =
            "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
        private const string CoreFoundation =
            "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        [DllImport(ApplicationServices)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool AXIsProcessTrusted();

        [DllImport(ApplicationServices)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool AXIsProcessTrustedWithOptions(nint options);

        [DllImport(ApplicationServices)]
        public static extern nint AXUIElementCreateApplication(int processIdentifier);

        [DllImport(ApplicationServices)]
        public static extern int AXUIElementCopyAttributeValue(
            nint element,
            nint attribute,
            out nint value);

        [DllImport(ApplicationServices)]
        public static extern int AXObserverCreate(
            int application,
            AxObserverCallback callback,
            out nint observer);

        [DllImport(ApplicationServices)]
        public static extern int AXObserverAddNotification(
            nint observer,
            nint element,
            nint notification,
            nint refcon);

        [DllImport(ApplicationServices)]
        public static extern int AXObserverRemoveNotification(
            nint observer,
            nint element,
            nint notification);

        [DllImport(ApplicationServices)]
        public static extern nint AXObserverGetRunLoopSource(nint observer);

        [DllImport(CoreFoundation)]
        public static extern nint CFStringCreateWithCString(
            nint allocator,
            string value,
            uint encoding);

        [DllImport(CoreFoundation)]
        public static extern nint CFDictionaryCreate(
            nint allocator,
            nint[] keys,
            nint[] values,
            nint count,
            nint keyCallbacks,
            nint valueCallbacks);

        [DllImport(CoreFoundation)]
        public static extern nint CFStringGetLength(nint value);

        [DllImport(CoreFoundation)]
        public static extern nint CFStringGetMaximumSizeForEncoding(nint length, uint encoding);

        [DllImport(CoreFoundation)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool CFStringGetCString(
            nint value,
            byte[] buffer,
            nint bufferSize,
            uint encoding);

        [DllImport(CoreFoundation)]
        public static extern void CFRelease(nint value);

        [DllImport(CoreFoundation)]
        public static extern nint CFRunLoopGetCurrent();

        [DllImport(CoreFoundation)]
        public static extern void CFRunLoopAddSource(nint runLoop, nint source, nint mode);

        [DllImport(CoreFoundation)]
        public static extern void CFRunLoopRemoveSource(nint runLoop, nint source, nint mode);

        [DllImport(CoreFoundation)]
        public static extern void CFRunLoopStop(nint runLoop);

        [DllImport(CoreFoundation)]
        public static extern int CFRunLoopRunInMode(
            nint mode,
            double seconds,
            [MarshalAs(UnmanagedType.I1)] bool returnAfterSourceHandled);
    }
}
