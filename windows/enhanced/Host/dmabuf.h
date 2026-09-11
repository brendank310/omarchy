#pragma once
#include <gtk/gtk.h>
GtkWidget *dmabuf_area_new(void);
gboolean dmabuf_connect(const char *address, const char *relay_path, GError **error);
void dmabuf_crop(int x, int y, int width, int height, gboolean visible);
void dmabuf_size(int *width, int *height);
gboolean dmabuf_ready(void);
void dmabuf_input(const char *interface, const char *method, GVariant *parameters);
void dmabuf_ui_size(int width, int height);
void dmabuf_disconnect(void);
