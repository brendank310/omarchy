/* Exercise the actual GTK event handler without creating a display. */
#define main viewer_main
#include "viewer.c"
#undef main

static guint events, last_code;
static int crop_width, crop_height;
GtkWidget *dmabuf_area_new(void) { return NULL; }
gboolean dmabuf_connect(const char *a, const char *b, GError **e) { return TRUE; }
void dmabuf_crop(int a, int b, int c, int d, gboolean e) { crop_width = c; crop_height = d; }
void dmabuf_size(int *w, int *h) { *w = 1280; *h = 800; }
gboolean dmabuf_ready(void) { return TRUE; }
void dmabuf_ui_size(int w, int h) {}
void dmabuf_disconnect(void) {}
void dmabuf_input(const char *interface, const char *method, GVariant *parameters) {
  g_assert_cmpstr(interface, ==, "Keyboard");
  g_variant_get(parameters, "(u)", &last_code);
  g_assert_cmpuint(last_code, !=, 0xdb);
  g_assert_cmpuint(last_code, !=, 0xdc);
  events++;
  g_variant_ref_sink(parameters); g_variant_unref(parameters);
}
static void send_key(guint hardware, guint state, GdkEventType type) {
  GdkEventKey event = {.hardware_keycode = hardware, .state = state, .type = type};
  key(NULL, &event, NULL);
}
static gboolean closes(const char *json) {
  JsonParser *parser = json_parser_new();
  g_assert_true(json_parser_load_from_data(parser, json, -1, NULL));
  gboolean closed = selected_closed(json_node_get_object(json_parser_get_root(parser)));
  g_object_unref(parser);
  return closed;
}
int main(void) {
  epoch = "session"; selected = "aa"; pid = 10;
  g_assert_true(closes("{\"windows\":[{\"id\":\"desktop\",\"processId\":20}]}"));
  g_assert_false(closes("{\"windows\":[{\"id\":\"aa\",\"processId\":10,\"minimized\":true}]}"));
  g_assert_false(closes("{\"windows\":[]}"));
  g_assert_false(closes("{\"windows\":[{\"id\":\"desktop\",\"processId\":20}],\"capacityReached\":true}"));
  g_assert_true(closes("{\"windows\":[{\"id\":\"aa\",\"processId\":99}]}"));
  desktop_mode = TRUE;
  g_assert_false(closes("{\"windows\":[{\"id\":\"desktop\",\"processId\":20}]}"));
  desktop_mode = FALSE;
  g_print("ok - closed application exits; minimize, privacy and desktop mode do not\n");
  host_active = available = focused = TRUE;
  send_key(133, 0, GDK_KEY_PRESS);
  send_key(133, GDK_SUPER_MASK, GDK_KEY_RELEASE);
  send_key(134, 0, GDK_KEY_PRESS);
  send_key(134, GDK_SUPER_MASK, GDK_KEY_RELEASE);
  send_key(38, GDK_SUPER_MASK, GDK_KEY_PRESS);
  send_key(38, 0, GDK_KEY_RELEASE);
  g_assert_cmpuint(events, ==, 0);
  send_key(38, GDK_CONTROL_MASK, GDK_KEY_PRESS);
  g_assert_cmpuint(events, ==, 2);
  g_assert_cmpuint(last_code, ==, 30);
  send_key(133, GDK_CONTROL_MASK, GDK_KEY_PRESS);
  g_assert_cmpuint(events, ==, 4);
  g_assert_false(pressed[29]); g_assert_false(pressed[30]);
  send_key(38, 0, GDK_KEY_RELEASE);
  g_assert_cmpuint(events, ==, 4);
  g_print("ok - Super stays on host; host chords release guest input; Ctrl+A still forwards\n");
  desktop_mode = TRUE;
  desktop_update(NULL);
  g_assert_cmpint(crop_width, ==, 1280); g_assert_cmpint(crop_height, ==, 800);
  g_assert_true(available); g_assert_true(focused);
  received(NULL);
  g_assert_true(available);
  control("window.resize", 600, 400);
  g_assert_true(g_queue_is_empty(&writes));
  g_print("ok - desktop mode uses full scanout without application control or metadata gating\n");
  return 0;
}
