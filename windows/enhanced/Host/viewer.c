#define G_LOG_DOMAIN "omarchy-viewer"
#include <gtk/gtk.h>
#include "dmabuf.h"
#include <json-glib/json-glib.h>
#include <gio/gunixsocketaddress.h>
#include <math.h>
#include <glib-unix.h>
#include <signal.h>

/* QEMU supplies DMA-BUF scanout and input. The guest channel supplies only
 * window identity, geometry, focus and resize control. GTK runs on main. */
static GtkWidget *window, *area;
static GSocketConnection *metadata;
static char *epoch, *selected, *focus_target, *last_foreground;
static guint pid, focus_pid, buttons, resize_timer;
static int x, y, width, height, framebuffer_width, framebuffer_height;
static gboolean available, focused;
static gboolean host_active, focus_pending;
static gboolean desktop_mode;
static gboolean cursor_hidden;
static GdkCursor *blank_cursor;
static char *focus_request;
static gboolean pressed[512];
static guint serial;
static gint64 resize_deadline;
static gboolean resize(gpointer unused);
static GMutex pending_mutex;
static GCond pending_cond;
static gboolean pending;
static GQueue writes = G_QUEUE_INIT;
static gboolean writing;
static GCancellable *write_cancel;
static guint write_timeout;
static void release_input(void);
static void host_cursor(gboolean hide);

static gboolean cancel_write(gpointer unused) {
  write_timeout = 0;
  g_cancellable_cancel(write_cancel);
  return G_SOURCE_REMOVE;
}
static void write_next(void);
static void write_done(GObject *stream, GAsyncResult *result, gpointer bytes) {
  GError *error = NULL;
  gboolean ok = g_output_stream_write_all_finish(G_OUTPUT_STREAM(stream), result, NULL, &error);
  if (write_timeout) { g_source_remove(write_timeout); write_timeout = 0; }
  g_clear_object(&write_cancel);
  g_bytes_unref(bytes); writing = FALSE;
  if (!ok) {
    g_warning("Metadata write failed: %s", error->message); g_clear_error(&error);
    available = FALSE; release_input(); dmabuf_crop(0, 0, 0, 0, FALSE);
    g_queue_clear_full(&writes, (GDestroyNotify)g_bytes_unref);
    g_io_stream_close(G_IO_STREAM(metadata), NULL, NULL);
  } else write_next();
}
static void write_next(void) {
  if (writing || g_queue_is_empty(&writes)) return;
  GBytes *bytes = g_queue_pop_head(&writes);
  gsize size;
  const void *data = g_bytes_get_data(bytes, &size);
  writing = TRUE;
  write_cancel = g_cancellable_new();
  write_timeout = g_timeout_add_seconds(3, cancel_write, NULL);
  g_output_stream_write_all_async(g_io_stream_get_output_stream(G_IO_STREAM(metadata)), data, size,
    G_PRIORITY_DEFAULT, write_cancel, write_done, bytes);
}

static const char *string_member(JsonObject *o, const char *key) {
  return json_object_has_member(o, key) ? json_object_get_string_member(o, key) : "";
}

static void control(const char *method, int w, int h) {
  if (desktop_mode) return;
  if (!epoch || !pid || (!available && strcmp(method, "window.focus"))) return;
  JsonObject *o = json_object_new();
  json_object_set_int_member(o, "v", 1);
  char id[32]; g_snprintf(id, sizeof id, "viewer-%u", ++serial);
  if (!strcmp(method, "window.focus")) {
    g_free(focus_request); focus_request = g_strdup(id);
    focused = FALSE;
  }
  json_object_set_string_member(o, "id", id);
  json_object_set_string_member(o, "session", epoch);
  json_object_set_string_member(o, "method", method);
  gboolean focus_command = !strcmp(method, "window.focus") && focus_target;
  json_object_set_string_member(o, "windowId", focus_command ? focus_target : selected);
  json_object_set_int_member(o, "processId", focus_command ? focus_pid : pid);
  if (w > 0) { json_object_set_int_member(o, "width", w); json_object_set_int_member(o, "height", h); }
  JsonNode *node = json_node_new(JSON_NODE_OBJECT); json_node_take_object(node, o);
  char *data = json_to_string(node, FALSE);
  guint32 length = GUINT32_TO_LE(strlen(data));
  if (g_queue_get_length(&writes) >= 32) {
    g_warning("Guest control queue is full");
    available = FALSE; release_input(); dmabuf_crop(0, 0, 0, 0, FALSE);
    if (write_cancel) g_cancellable_cancel(write_cancel);
  } else {
    gsize size = strlen(data);
    char *frame = g_malloc(size + 4);
    memcpy(frame, &length, 4); memcpy(frame + 4, data, size);
    g_queue_push_tail(&writes, g_bytes_new_take(frame, size + 4));
    write_next();
  }
  g_free(data); json_node_free(node);
}

static void release_input(void) {
  for (guint i = 0; i < G_N_ELEMENTS(pressed); i++) if (pressed[i]) {
    dmabuf_input("Keyboard", "Release", g_variant_new("(u)", i)); pressed[i] = FALSE;
  }
  for (int i = 1; i <= 3; i++) if (buttons & (1u << (i - 1))) {
    buttons &= ~(1u << (i - 1)); dmabuf_input("Mouse", "Release", g_variant_new("(u)", i - 1));
  }
}

static void crop_window(JsonObject *item) {
  JsonObject *frame = item;
  if (json_object_has_member(item, "frame") && JSON_NODE_HOLDS_OBJECT(json_object_get_member(item, "frame")))
    frame = json_object_get_object_member(item, "frame");
  x = json_object_get_int_member(frame, "x"); y = json_object_get_int_member(frame, "y");
  width = json_object_get_int_member(frame, "width"); height = json_object_get_int_member(frame, "height");
  if (!focus_target || strcmp(focus_target, string_member(item, "id")))
    g_debug("Guest view: id=%s class=%s frame=%d,%d %dx%d", string_member(item, "id"),
      string_member(item, "className"), x, y, width, height);
  g_free(focus_target); focus_target = g_strdup(string_member(item, "id"));
  focus_pid = json_object_get_int_member(item, "processId");
}

static gboolean owned_by_selected(JsonArray *windows, JsonObject *item) {
  for (guint depth = 0; depth < json_array_get_length(windows); depth++) {
    const char *owner = string_member(item, "ownerId");
    if (!owner || !*owner) return FALSE;
    if (!strcmp(owner, selected)) return TRUE;
    JsonObject *next = NULL;
    for (guint i = 0; i < json_array_get_length(windows); i++) {
      JsonObject *candidate = json_array_get_object_element(windows, i);
      if (!strcmp(owner, string_member(candidate, "id"))) { next = candidate; break; }
    }
    if (!next) return FALSE;
    item = next;
  }
  return FALSE;
}

static gboolean selected_closed(JsonObject *snapshot) {
  if (desktop_mode || !epoch || !pid) return FALSE;
  JsonArray *windows = json_object_get_array_member(snapshot, "windows");
  /* Locked/secure desktops publish an empty list. A truncated list also
   * cannot prove that a window closed. Minimized windows remain listed. */
  if (!json_array_get_length(windows) ||
      (json_object_has_member(snapshot, "capacityReached") && json_object_get_boolean_member(snapshot, "capacityReached"))) return FALSE;
  for (guint i = 0; i < json_array_get_length(windows); i++) {
    JsonObject *item = json_array_get_object_element(windows, i);
    if (!strcmp(string_member(item, "id"), selected) &&
        json_object_get_int_member(item, "processId") == pid) return FALSE;
  }
  return TRUE;
}

static gboolean received(gpointer data) {
  JsonNode *node = data;
  if (desktop_mode) goto complete;
  if (node && JSON_NODE_HOLDS_OBJECT(node)) {
    JsonObject *o = json_node_get_object(node);
    const char *type = string_member(o, "type");
    if (!strcmp(type, "session.ready")) {
      g_queue_clear_full(&writes, (GDestroyNotify)g_bytes_unref);
      release_input(); available = FALSE; g_free(epoch); epoch = g_strdup(string_member(o, "session"));
      g_clear_pointer(&focus_request, g_free); focused = FALSE; focus_pending = host_active;
    } else if (!strcmp(type, "hello") || !strcmp(type, "session.unavailable")) {
      available = FALSE; release_input();
      g_clear_pointer(&epoch, g_free); g_clear_pointer(&focus_request, g_free); focused = FALSE;
    } else if (!strcmp(type, "windows.snapshot")) {
      if (selected_closed(o)) {
        gtk_widget_destroy(window);
        goto complete;
      }
      gboolean found = FALSE;
      JsonArray *windows = json_object_get_array_member(o, "windows");
      for (guint i = 0; i < json_array_get_length(windows); i++) {
        JsonObject *item = json_array_get_object_element(windows, i);
        if (json_object_get_boolean_member(item, "foreground") &&
            (!last_foreground || strcmp(last_foreground, string_member(item, "id")))) {
          g_free(last_foreground); last_foreground = g_strdup(string_member(item, "id"));
          g_debug("Guest foreground: id=%s class=%s owner=%s", last_foreground,
            string_member(item, "className"), string_member(item, "ownerId"));
        }
      }
      for (guint i = 0; i < json_array_get_length(windows); i++) {
        JsonObject *item = json_array_get_object_element(windows, i);
        if (strcmp(string_member(item, "id"), selected)) continue;
        guint next_pid = json_object_get_int_member(item, "processId");
        if (pid && pid != next_pid) break;
        pid = next_pid;
        crop_window(item);
        found = !json_object_get_boolean_member(item, "minimized") && width > 0 && height > 0;
        focused = json_object_get_boolean_member(item, "foreground");
        gtk_window_set_title(GTK_WINDOW(window), string_member(item, "title"));
        break;
      }
      /* Keep modal application dialogs usable in the same host window. The
       * main window still owns resize requests; only the view/focus target
       * changes while an owned dialog is in the foreground. */
      if (found) for (guint i = 0; i < json_array_get_length(windows); i++) {
        JsonObject *item = json_array_get_object_element(windows, i);
        if (json_object_get_boolean_member(item, "foreground") && owned_by_selected(windows, item)) {
          crop_window(item); focused = TRUE; break;
        }
      }
      gboolean first = found && !available;
      available = found;
      focused = focused && host_active && !focus_request;
      if (focus_pending && host_active) {
        control("window.focus", 0, 0); focus_pending = FALSE;
      }
      if (first && !resize_timer) resize_timer = g_timeout_add(150, resize, NULL);
      if (!available || !focused) release_input();
    } else if (focus_request && !strcmp(string_member(o, "id"), focus_request) && json_object_has_member(o, "result")) {
      focused = host_active;
      g_clear_pointer(&focus_request, g_free);
      g_debug("Guest focus acknowledged for %s", focus_target ? focus_target : selected);
    } else if (json_object_has_member(o, "error")) {
      if (focus_request && !strcmp(string_member(o, "id"), focus_request)) {
        g_clear_pointer(&focus_request, g_free); focused = FALSE; release_input();
      }
      g_warning("Guest command: %s: %s", string_member(o, "error"), string_member(o, "message"));
    }
  } else { available = FALSE; release_input(); gtk_window_set_title(GTK_WINDOW(window), "Windows channel disconnected"); }
  dmabuf_crop(x, y, width, height, available);
  if (!available) host_cursor(FALSE);
complete:
  if (node) json_node_free(node);
  g_mutex_lock(&pending_mutex); pending = FALSE; g_cond_signal(&pending_cond); g_mutex_unlock(&pending_mutex);
  return G_SOURCE_REMOVE;
}

static gpointer read_metadata(gpointer unused) {
  GInputStream *in = g_io_stream_get_input_stream(G_IO_STREAM(metadata));
  for (;;) {
    guint32 size; gsize count; JsonNode *node = NULL;
    gboolean ok = g_input_stream_read_all(in, &size, 4, &count, NULL, NULL) && count == 4;
    if (ok) {
      size = GUINT32_FROM_LE(size); ok = size > 0 && size <= 1024 * 1024;
      if (ok) {
        char *data = g_malloc(size);
        ok = g_input_stream_read_all(in, data, size, &count, NULL, NULL) && count == size;
        JsonParser *parser = json_parser_new();
        if (ok) ok = json_parser_load_from_data(parser, data, size, NULL);
        if (ok) node = json_node_copy(json_parser_get_root(parser));
        g_object_unref(parser); g_free(data);
      }
    }
    g_mutex_lock(&pending_mutex); pending = TRUE; g_idle_add(received, node);
    while (pending) g_cond_wait(&pending_cond, &pending_mutex);
    g_mutex_unlock(&pending_mutex);
    if (!ok) break;
  }
  return NULL;
}

static void host_cursor(gboolean hide) {
  if (hide == cursor_hidden || !area || !gtk_widget_get_realized(area)) return;
  GdkWindow *surface = gtk_widget_get_window(area);
  if (hide && !blank_cursor)
    blank_cursor = gdk_cursor_new_for_display(gdk_window_get_display(surface), GDK_BLANK_CURSOR);
  gdk_window_set_cursor(surface, hide ? blank_cursor : NULL);
  cursor_hidden = hide;
}

static gboolean position(double px, double py) {
  if (!host_active || !available || !dmabuf_ready()) { host_cursor(FALSE); return FALSE; }
  dmabuf_size(&framebuffer_width, &framebuffer_height);
  int w = gtk_widget_get_allocated_width(area), h = gtk_widget_get_allocated_height(area);
  double scale = MIN((double)w / width, (double)h / height);
  int gx = (px - (w - width * scale) / 2) / scale, gy = (py - (h - height * scale) / 2) / scale;
  if (gx < 0 || gy < 0 || gx >= width || gy >= height || x + gx < 0 || y + gy < 0 || x + gx >= framebuffer_width || y + gy >= framebuffer_height) { host_cursor(FALSE); return FALSE; }
  /* Windows already draws its cursor into the shared framebuffer. */
  host_cursor(TRUE);
  dmabuf_input("Mouse", "SetAbsPosition", g_variant_new("(uu)", (guint)(x + gx), (guint)(y + gy))); return TRUE;
}
static gboolean motion(GtkWidget *widget, GdkEventMotion *event, gpointer unused) { position(event->x, event->y); return TRUE; }
static gboolean crossing(GtkWidget *widget, GdkEventCrossing *event, gpointer unused) {
  if (event->type == GDK_ENTER_NOTIFY) position(event->x, event->y);
  else host_cursor(FALSE);
  return FALSE;
}
static gboolean button(GtkWidget *widget, GdkEventButton *event, gpointer unused) {
  g_debug("Pointer button=%u event=%u focused=%d available=%d", event->button, event->type, focused, available);
  if (event->button < 1 || event->button > 3) return TRUE;
  guint bit = 1u << (event->button - 1);
  if (event->type == GDK_BUTTON_RELEASE) {
    if (buttons & bit) dmabuf_input("Mouse", "Release", g_variant_new("(u)", event->button - 1));
    buttons &= ~bit; return TRUE;
  }
  if (!position(event->x, event->y)) return TRUE;
  gtk_widget_grab_focus(area);
  if (!focused) { control("window.focus", 0, 0); return TRUE; }
  if (event->type == GDK_BUTTON_PRESS) { buttons |= bit; dmabuf_input("Mouse", "Press", g_variant_new("(u)", event->button - 1)); }
  return TRUE;
}
static gboolean scroll(GtkWidget *widget, GdkEventScroll *event, gpointer unused) {
  if (!position(event->x, event->y) || !focused) return TRUE;
  int b = event->direction == GDK_SCROLL_UP ? 4 : 5;
  dmabuf_input("Mouse", "Press", g_variant_new("(u)", b - 1)); dmabuf_input("Mouse", "Release", g_variant_new("(u)", b - 1)); return TRUE;
}
static guint scancode(guint hardware) {
  guint evdev = hardware >= 8 ? hardware - 8 : 0;
  if (evdev < 89) return evdev;
  switch (evdev) {
    case 96:return 0x9c; case 97:return 0x9d; case 98:return 0xb5; case 100:return 0xb8;
    case 102:return 0xc7; case 103:return 0xc8; case 104:return 0xc9; case 105:return 0xcb;
    case 106:return 0xcd; case 107:return 0xcf; case 108:return 0xd0; case 109:return 0xd1;
    case 110:return 0xd2; case 111:return 0xd3; case 125:return 0xdb; case 126:return 0xdc;
    default:return 0;
  }
}
static void modifier(guint state, guint mask, guint left, guint right) {
  if (state & mask) {
    if (!pressed[left] && !pressed[right]) {
      pressed[left] = TRUE;
      dmabuf_input("Keyboard", "Press", g_variant_new("(u)", left));
    }
  } else {
    guint codes[] = {left, right};
    for (guint i = 0; i < G_N_ELEMENTS(codes); i++) if (pressed[codes[i]]) {
      pressed[codes[i]] = FALSE;
      dmabuf_input("Keyboard", "Release", g_variant_new("(u)", codes[i]));
    }
  }
}
static gboolean key(GtkWidget *widget, GdkEventKey *event, gpointer unused) {
  guint code = scancode(event->hardware_keycode);
  /* Super belongs to Hyprland. Forwarding even an isolated release after a
   * host shortcut can open Start and steal focus from the selected app. */
  if (code == 0xdb || code == 0xdc || (event->state & GDK_SUPER_MASK)) {
    release_input(); return TRUE;
  }
  if (!host_active || !dmabuf_ready() || !available || !focused || !code) return TRUE;
  /* Wayland can communicate modifiers without individual key transitions,
   * including when focus changes while a modifier is held. */
  if (code != 42 && code != 54) modifier(event->state, GDK_SHIFT_MASK, 42, 54);
  if (code != 29 && code != 0x9d) modifier(event->state, GDK_CONTROL_MASK, 29, 0x9d);
  if (code != 56 && code != 0xb8) modifier(event->state, GDK_MOD1_MASK, 56, 0xb8);
  if (event->type == GDK_KEY_PRESS) { pressed[code] = TRUE; dmabuf_input("Keyboard", "Press", g_variant_new("(u)", code)); }
  else if (pressed[code]) { pressed[code] = FALSE; dmabuf_input("Keyboard", "Release", g_variant_new("(u)", code)); }
  return TRUE;
}
static gboolean focus_out(GtkWidget *widget, GdkEventFocus *event, gpointer unused) { release_input(); return FALSE; }
static void activation(GObject *object, GParamSpec *property, gpointer unused) {
  host_active = gtk_window_is_active(GTK_WINDOW(window));
  release_input();
  if (desktop_mode) { focused = host_active; return; }
  if (host_active) {
    focus_pending = TRUE;
    if (pid && epoch) { control("window.focus", 0, 0); focus_pending = FALSE; }
  } else {
    focus_pending = FALSE;
    focused = FALSE;
  }
}
static gboolean resize(gpointer unused) {
  if (desktop_mode) { resize_timer = 0; return G_SOURCE_REMOVE; }
  dmabuf_size(&framebuffer_width, &framebuffer_height);
  if (!resize_deadline) resize_deadline = g_get_monotonic_time() + 10 * G_TIME_SPAN_SECOND;
  if (!framebuffer_width || !framebuffer_height) {
    if (g_get_monotonic_time() < resize_deadline) return G_SOURCE_CONTINUE;
    g_warning("No scanout received for application resizing");
    resize_timer = 0; resize_deadline = 0; return G_SOURCE_REMOVE;
  }
  /* Guest coordinates use the guest's current DPI. Match the host logical
   * allocation so a 100% Windows desktop remains legible on a scaled host. */
  int w = gtk_widget_get_allocated_width(area), h = gtk_widget_get_allocated_height(area);
  w = CLAMP(w, 200, 2048); h = CLAMP(h, 120, 1536);
  resize_timer = 0; resize_deadline = 0;
  g_message("Resize guest window: %dx%d (scanout %dx%d)", w, h, framebuffer_width, framebuffer_height);
  control("window.resize", w, h); return G_SOURCE_REMOVE;
}
static void allocated(GtkWidget *widget, GdkRectangle *allocation, gpointer unused) {
  if (resize_timer) g_source_remove(resize_timer);
  resize_deadline = 0;
  resize_timer = g_timeout_add(150, resize, NULL);
}
static void destroy(GtkWidget *widget, gpointer unused) { release_input(); dmabuf_disconnect(); gtk_main_quit(); }
static gboolean stop(gpointer unused) { gtk_widget_destroy(window); return G_SOURCE_REMOVE; }

static gboolean desktop_update(gpointer unused) {
  dmabuf_size(&framebuffer_width, &framebuffer_height);
  x = y = 0; width = framebuffer_width; height = framebuffer_height;
  available = dmabuf_ready() && width > 0 && height > 0;
  if (!available) host_cursor(FALSE);
  focused = host_active;
  dmabuf_crop(x, y, width, height, available);
  return G_SOURCE_CONTINUE;
}

int main(int argc, char **argv) {
  if (argc != 4 && argc != 5) { g_printerr("Usage: %s DBUS_ADDRESS METADATA_UNIX_SOCKET WINDOW_ID [LISTENER_RELAY_SOCKET]\n", argv[0]); return 2; }
  selected = g_strdup(argv[3]);
  desktop_mode = !strcmp(selected, "desktop");
  g_setenv("GDK_BACKEND", "wayland", TRUE);
  gtk_init(&argc, &argv);
  GSocketClient *client = g_socket_client_new();
  GSocketAddress *address = g_unix_socket_address_new(argv[2]); GError *error = NULL;
  metadata = g_socket_client_connect(client, G_SOCKET_CONNECTABLE(address), NULL, &error);
  g_object_unref(address); g_object_unref(client);
  if (!metadata) { g_printerr("Metadata: %s\n", error->message); g_error_free(error); return 1; }
  window = gtk_window_new(GTK_WINDOW_TOPLEVEL); gtk_window_set_default_size(GTK_WINDOW(window), 1000, 700);
  gtk_window_set_title(GTK_WINDOW(window), desktop_mode ? "Windows Desktop" : "Windows application — connecting");
  area = dmabuf_area_new(); gtk_widget_set_can_focus(area, TRUE); gtk_container_add(GTK_CONTAINER(window), area);
  gtk_widget_add_events(area, GDK_POINTER_MOTION_MASK | GDK_ENTER_NOTIFY_MASK | GDK_LEAVE_NOTIFY_MASK | GDK_BUTTON_PRESS_MASK | GDK_BUTTON_RELEASE_MASK | GDK_SCROLL_MASK | GDK_KEY_PRESS_MASK | GDK_KEY_RELEASE_MASK | GDK_FOCUS_CHANGE_MASK);
  g_signal_connect(area,"enter-notify-event",G_CALLBACK(crossing),NULL); g_signal_connect(area,"leave-notify-event",G_CALLBACK(crossing),NULL);
  g_signal_connect(area,"motion-notify-event",G_CALLBACK(motion),NULL);
  g_signal_connect(area,"button-press-event",G_CALLBACK(button),NULL); g_signal_connect(area,"button-release-event",G_CALLBACK(button),NULL);
  g_signal_connect(area,"scroll-event",G_CALLBACK(scroll),NULL); g_signal_connect(area,"key-press-event",G_CALLBACK(key),NULL);
  g_signal_connect(area,"key-release-event",G_CALLBACK(key),NULL); g_signal_connect(area,"focus-out-event",G_CALLBACK(focus_out),NULL);
  g_signal_connect(window,"notify::is-active",G_CALLBACK(activation),NULL);
  g_signal_connect(area,"size-allocate",G_CALLBACK(allocated),NULL); g_signal_connect(window,"destroy",G_CALLBACK(destroy),NULL);
  g_unix_signal_add(SIGINT, stop, NULL); g_unix_signal_add(SIGTERM, stop, NULL);
  gtk_widget_show_all(window); gtk_widget_grab_focus(area);
  if (!dmabuf_connect(argv[1], argc == 5 ? argv[4] : NULL, &error)) { g_printerr("D-Bus display: %s\n", error->message); return 1; }
  if (desktop_mode) g_timeout_add(100, desktop_update, NULL);
  g_thread_unref(g_thread_new("window-metadata",read_metadata,NULL));
  gtk_main();
  return 0;
}
