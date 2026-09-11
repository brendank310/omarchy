.RECIPEPREFIX := >
CC ?= cc
CFLAGS += -O2 -g -Wall -Wextra -Werror -Wno-unused-parameter
PKGS = gtk+-3.0 epoxy gio-unix-2.0 json-glib-1.0
all: enhanced-viewer
enhanced-viewer: viewer.c dmabuf.c dmabuf.h
>$(CC) $(CFLAGS) $(shell pkg-config --cflags $(PKGS)) viewer.c dmabuf.c -o $@ $(shell pkg-config --libs $(PKGS)) -lm
clean:
>$(RM) enhanced-viewer test-input
test-input: test-input.c viewer.c dmabuf.h
>$(CC) $(CFLAGS) $(shell pkg-config --cflags $(PKGS)) test-input.c -o $@ $(shell pkg-config --libs $(PKGS)) -lm
check-input: test-input
>./test-input
