using System;
using System.Runtime.InteropServices;

namespace PopupNotify.Native
{
    /// <summary>
    /// The slice of the Tizen EFL stack this popup needs, P/Invoked the same way
    /// Vetro's launcher does it (dock/src/Native/Efl.cs). EFL 1.25 ships on the TV;
    /// every symbol here is one the launcher already uses, plus evas textblock for
    /// the wrapped message. Eina_Bool is an unsigned char, so callbacks return byte
    /// and boolean arguments are marshalled as U1.
    /// </summary>
    internal static class Efl
    {
        private const string Elm = "libelementary.so.1";
        private const string Evas = "libevas.so.1";
        private const string Ecore = "libecore.so.1";
        private const string EflUtil = "libcapi-ui-efl-util.so.0";

        // elm_win_type: 12 is ELM_WIN_NOTIFICATION, the platform's own popup surface.
        public const int ElmWinNotification = 12;
        public const int EvasCallbackKeyDown = 10;
        public const int EcoreFdRead = 1;
        public const int EvasLoadErrorNone = 0;
        public const int EvasImageScaleHintDynamic = 1;
        public const int EvasTextStyleSoftShadow = 8;

        // --- delegates (kept alive by their owners; the GC must not collect a callback in use) ---
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate byte TaskCb(IntPtr data);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate byte FdCb(IntPtr data, IntPtr handler);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void EvasCb(IntPtr data, IntPtr evas, IntPtr obj, IntPtr eventInfo);

        // --- elementary: the window ---
        [DllImport(Elm)] public static extern int elm_init(int argc, IntPtr argv);
        [DllImport(Elm)] public static extern void elm_run();
        [DllImport(Elm)] public static extern void elm_exit();
        [DllImport(Elm)] public static extern void elm_shutdown();
        [DllImport(Elm)] public static extern IntPtr elm_win_add(IntPtr parent, string name, int type);
        [DllImport(Elm)] public static extern void elm_win_title_set(IntPtr win, string title);
        [DllImport(Elm)] public static extern void elm_win_alpha_set(IntPtr win, [MarshalAs(UnmanagedType.U1)] bool alpha);
        [DllImport(Elm)] public static extern void elm_win_borderless_set(IntPtr win, [MarshalAs(UnmanagedType.U1)] bool borderless);
        [DllImport(Elm)] public static extern void elm_win_prop_focus_skip_set(IntPtr win, [MarshalAs(UnmanagedType.U1)] bool skip);
        [DllImport(Elm)] public static extern void elm_win_layer_set(IntPtr win, int layer);
        [DllImport(Elm)] public static extern int elm_win_aux_hint_add(IntPtr win, string hint, string val);
        [DllImport(Elm)] public static extern void elm_win_activate(IntPtr win);
        [DllImport(Elm)] public static extern void elm_win_screen_size_get(IntPtr win, out int x, out int y, out int w, out int h);

        // --- evas: generic objects ---
        [DllImport(Evas)] public static extern IntPtr evas_object_evas_get(IntPtr obj);
        [DllImport(Evas)] public static extern void evas_object_move(IntPtr obj, int x, int y);
        [DllImport(Evas)] public static extern void evas_object_resize(IntPtr obj, int w, int h);
        [DllImport(Evas)] public static extern void evas_object_show(IntPtr obj);
        [DllImport(Evas)] public static extern void evas_object_hide(IntPtr obj);
        [DllImport(Evas)] public static extern void evas_object_del(IntPtr obj);
        [DllImport(Evas)] public static extern void evas_object_color_set(IntPtr obj, int r, int g, int b, int a);
        [DllImport(Evas)] public static extern void evas_object_raise(IntPtr obj);
        [DllImport(Evas)] public static extern void evas_object_lower(IntPtr obj);
        [DllImport(Evas)] public static extern void evas_object_event_callback_add(IntPtr obj, int type, EvasCb cb, IntPtr data);
        [DllImport(Evas)] public static extern IntPtr evas_object_rectangle_add(IntPtr evas);

        // --- evas: images ---
        [DllImport(Evas)] public static extern IntPtr evas_object_image_add(IntPtr evas);
        [DllImport(Evas)] public static extern void evas_object_image_file_set(IntPtr img, string file, string key);
        [DllImport(Evas)] public static extern int evas_object_image_load_error_get(IntPtr img);
        [DllImport(Evas)] public static extern void evas_object_image_filled_set(IntPtr img, [MarshalAs(UnmanagedType.U1)] bool filled);
        [DllImport(Evas)] public static extern void evas_object_image_smooth_scale_set(IntPtr img, [MarshalAs(UnmanagedType.U1)] bool smooth);
        [DllImport(Evas)] public static extern void evas_object_image_scale_hint_set(IntPtr img, int hint);
        [DllImport(Evas)] public static extern void evas_object_image_size_get(IntPtr img, out int w, out int h);
        [DllImport(Evas)] public static extern void evas_object_image_size_set(IntPtr img, int w, int h);
        [DllImport(Evas)] public static extern int evas_object_image_stride_get(IntPtr img);
        [DllImport(Evas)] public static extern IntPtr evas_object_image_data_get(IntPtr img, [MarshalAs(UnmanagedType.U1)] bool forWriting);
        [DllImport(Evas)] public static extern void evas_object_image_data_set(IntPtr img, IntPtr data);
        [DllImport(Evas)] public static extern void evas_object_image_data_update_add(IntPtr img, int x, int y, int w, int h);
        [DllImport(Evas)] public static extern void evas_object_image_alpha_set(IntPtr img, [MarshalAs(UnmanagedType.U1)] bool alpha);

        // --- evas: single-line text (titles and button labels) ---
        [DllImport(Evas)] public static extern IntPtr evas_object_text_add(IntPtr evas);
        [DllImport(Evas)] public static extern void evas_object_text_font_set(IntPtr txt, string font, int size);
        [DllImport(Evas)] public static extern void evas_object_text_text_set(IntPtr txt, string text);
        [DllImport(Evas)] public static extern int evas_object_text_horiz_advance_get(IntPtr txt);
        [DllImport(Evas)] public static extern int evas_object_text_vert_advance_get(IntPtr txt);
        [DllImport(Evas)] public static extern void evas_object_text_style_set(IntPtr txt, int style);
        [DllImport(Evas)] public static extern void evas_object_text_shadow_color_set(IntPtr txt, int r, int g, int b, int a);

        // --- evas: textblock (the wrapped message) ---
        [DllImport(Evas)] public static extern IntPtr evas_object_textblock_add(IntPtr evas);
        [DllImport(Evas)] public static extern IntPtr evas_textblock_style_new();
        [DllImport(Evas)] public static extern void evas_textblock_style_set(IntPtr style, string styleName);
        [DllImport(Evas)] public static extern void evas_object_textblock_style_set(IntPtr tb, IntPtr style);
        [DllImport(Evas)] public static extern void evas_object_textblock_text_markup_set(IntPtr tb, string markup);
        [DllImport(Evas)] public static extern void evas_object_textblock_size_native_get(IntPtr tb, out int w, out int h);
        [DllImport(Evas)] public static extern void evas_object_textblock_size_formatted_get(IntPtr tb, out int w, out int h);

        // --- ecore: the main loop's clock, timers and fd handlers ---
        [DllImport(Ecore)] public static extern double ecore_time_get();
        [DllImport(Ecore)] public static extern IntPtr ecore_animator_add(TaskCb func, IntPtr data);
        [DllImport(Ecore)] public static extern IntPtr ecore_animator_del(IntPtr animator);
        [DllImport(Ecore)] public static extern IntPtr ecore_timer_add(double inSeconds, TaskCb func, IntPtr data);
        [DllImport(Ecore)] public static extern IntPtr ecore_timer_del(IntPtr timer);
        [DllImport(Ecore)] public static extern IntPtr ecore_main_fd_handler_add(int fd, int flags, FdCb func, IntPtr data, IntPtr bufFunc, IntPtr bufData);
        [DllImport(Ecore)] public static extern IntPtr ecore_main_fd_handler_del(IntPtr handler);

        // --- platform: what makes the window a system notification the WM stacks on top ---
        [DllImport(EflUtil)] public static extern int efl_util_set_notification_window_level(IntPtr win, int level);
    }
}
