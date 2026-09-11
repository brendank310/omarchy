#include "dmabuf.h"
#include <epoxy/egl.h>
#include <epoxy/gl.h>
#include <gio/gunixfdlist.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <sys/time.h>
#include <unistd.h>
#include <stdint.h>

#define CONSOLE "/org/qemu/Display1/Console_0"
#define LISTENER "/org/qemu/Display1/Listener"
#define IFACE "org.qemu.Display1.Listener"
static GtkWidget *area;
static GDBusConnection *bus, *peer;
static EGLImageKHR image = EGL_NO_IMAGE_KHR;
static EGLDisplay display;
static GLuint texture, program, vao;
static int fb_width, fb_height, crop_x, crop_y, crop_width, crop_height;
static gboolean y0_top, visible;
static GDBusMethodInvocation *update;
static GDBusNodeInfo *schema;

static const char listener_xml[] =
 "<node><interface name='org.qemu.Display1.Listener'>"
 "<method name='ScanoutDMABUF'><arg type='h' direction='in'/><arg type='u' direction='in'/><arg type='u' direction='in'/><arg type='u' direction='in'/><arg type='u' direction='in'/><arg type='t' direction='in'/><arg type='b' direction='in'/></method>"
 "<method name='UpdateDMABUF'><arg type='i' direction='in'/><arg type='i' direction='in'/><arg type='i' direction='in'/><arg type='i' direction='in'/></method>"
 "<method name='Scanout'><arg type='u' direction='in'/><arg type='u' direction='in'/><arg type='u' direction='in'/><arg type='u' direction='in'/><arg type='ay' direction='in'/></method>"
 "<method name='Update'><arg type='i' direction='in'/><arg type='i' direction='in'/><arg type='i' direction='in'/><arg type='i' direction='in'/><arg type='u' direction='in'/><arg type='u' direction='in'/><arg type='ay' direction='in'/></method>"
 "<method name='Disable'/>"
 "<method name='MouseSet'><arg type='i' direction='in'/><arg type='i' direction='in'/><arg type='b' direction='in'/></method>"
 "<method name='CursorDefine'><arg type='i' direction='in'/><arg type='i' direction='in'/><arg type='i' direction='in'/><arg type='i' direction='in'/><arg type='ay' direction='in'/></method>"
 "<property name='Interfaces' type='as' access='read'/></interface></node>";

static void acknowledge(void) {
  if (update) {
    /* QEMU waits for this reply before reusing the scanout. Complete GPU reads
     * first. No CPU mapping, readback or framebuffer upload occurs here. */
    glFinish();
    g_dbus_method_invocation_return_value(update, NULL); g_clear_object(&update);
  }
}
static void discard_image(void) {
  acknowledge();
  if (texture) glDeleteTextures(1, &texture);
  texture = 0;
  if (image != EGL_NO_IMAGE_KHR) eglDestroyImageKHR(display, image);
  image = EGL_NO_IMAGE_KHR;
}
static GLuint shader(GLenum kind, const char *source) {
  GLuint id = glCreateShader(kind); glShaderSource(id, 1, &source, NULL); glCompileShader(id);
  GLint ok; glGetShaderiv(id, GL_COMPILE_STATUS, &ok);
  if (!ok) { char log[2048]; glGetShaderInfoLog(id, sizeof log, NULL, log); g_error("Shader: %s", log); }
  return id;
}
static void realize(GtkGLArea *widget, gpointer unused) {
  gtk_gl_area_make_current(widget);
  if (gtk_gl_area_get_error(widget)) return;
  display = eglGetCurrentDisplay();
  if (display == EGL_NO_DISPLAY || !epoxy_has_egl_extension(display, "EGL_EXT_image_dma_buf_import")) {
    GError *error = g_error_new_literal(G_IO_ERROR, G_IO_ERROR_NOT_SUPPORTED, "Wayland EGL DMA-BUF import is unavailable");
    gtk_gl_area_set_error(widget, error); g_error_free(error); return;
  }
  GLuint vertex = shader(GL_VERTEX_SHADER,
    "#version 330\nout vec2 uv; void main(){vec2 p[4]=vec2[4](vec2(-1,-1),vec2(1,-1),vec2(-1,1),vec2(1,1));vec2 v=p[gl_VertexID];gl_Position=vec4(v,0,1);uv=vec2((v.x+1)/2,(1-v.y)/2);}");
  GLuint fragment = shader(GL_FRAGMENT_SHADER,
    "#version 330\nin vec2 uv;out vec4 color;uniform sampler2D frame;uniform vec4 region;uniform bool top;void main(){vec2 t=region.xy+uv*region.zw;if(any(lessThan(t,vec2(0)))||any(greaterThan(t,vec2(1)))){color=vec4(0,0,0,1);return;}if(top)t.y=1-t.y;color=vec4(texture(frame,t).rgb,1);}");
  program = glCreateProgram(); glAttachShader(program, vertex); glAttachShader(program, fragment); glLinkProgram(program);
  glDeleteShader(vertex); glDeleteShader(fragment);
  GLint ok; glGetProgramiv(program, GL_LINK_STATUS, &ok); if (!ok) g_error("Shader linking failed");
  glGenVertexArrays(1, &vao);
  g_message("DMA-BUF viewer GL renderer: %s", glGetString(GL_RENDERER));
}
static gboolean render(GtkGLArea *widget, GdkGLContext *context, gpointer unused) {
  int scale = gtk_widget_get_scale_factor(GTK_WIDGET(widget));
  int w = gtk_widget_get_allocated_width(GTK_WIDGET(widget)) * scale, h = gtk_widget_get_allocated_height(GTK_WIDGET(widget)) * scale;
  glViewport(0, 0, w, h); glClearColor(.06,.06,.06,1); glClear(GL_COLOR_BUFFER_BIT);
  if (texture && visible && crop_width > 0 && crop_height > 0) {
    double ratio = MIN((double)w/crop_width, (double)h/crop_height);
    int vw = crop_width*ratio, vh = crop_height*ratio;
    glViewport((w-vw)/2, (h-vh)/2, vw, vh);
    glUseProgram(program); glActiveTexture(GL_TEXTURE0); glBindTexture(GL_TEXTURE_2D, texture);
    glUniform1i(glGetUniformLocation(program,"frame"),0);
    glUniform1i(glGetUniformLocation(program,"top"),y0_top);
    glUniform4f(glGetUniformLocation(program,"region"),(float)crop_x/fb_width,(float)crop_y/fb_height,(float)crop_width/fb_width,(float)crop_height/fb_height);
    glBindVertexArray(vao); glDrawArrays(GL_TRIANGLE_STRIP,0,4);
  }
  acknowledge(); return TRUE;
}
static void unrealize(GtkGLArea *widget, gpointer unused) {
  gtk_gl_area_make_current(widget); if (gtk_gl_area_get_error(widget)) return;
  discard_image(); if (program) glDeleteProgram(program); if (vao) glDeleteVertexArrays(1,&vao); program=vao=0;
}
static void method(GDBusConnection *connection, const char *sender, const char *path, const char *interface, const char *name, GVariant *args, GDBusMethodInvocation *invocation, gpointer unused) {
  gtk_gl_area_make_current(GTK_GL_AREA(area));
  if (gtk_gl_area_get_error(GTK_GL_AREA(area))) {
    g_dbus_method_invocation_return_error(invocation,G_IO_ERROR,G_IO_ERROR_NOT_SUPPORTED,"No usable EGL context"); return;
  }
  if (!strcmp(name,"ScanoutDMABUF")) {
    gint handle; guint w,h,stride,fourcc; guint64 modifier; gboolean top;
    g_variant_get(args,"(huuuutb)",&handle,&w,&h,&stride,&fourcc,&modifier,&top);
    if (!w || !h || w>16384 || h>16384 || !stride) {
      g_dbus_method_invocation_return_error(invocation,G_IO_ERROR,G_IO_ERROR_INVALID_DATA,"Invalid scanout dimensions");return;
    }
    GError *error=NULL;
    GUnixFDList *fds=g_dbus_message_get_unix_fd_list(g_dbus_method_invocation_get_message(invocation));
    int fd=fds?g_unix_fd_list_get(fds,handle,&error):-1;
    if(fd<0){g_dbus_method_invocation_return_error(invocation,G_IO_ERROR,G_IO_ERROR_INVALID_DATA,"Missing DMA-BUF fd");g_clear_error(&error);return;}
    EGLint attributes[32]={EGL_WIDTH,(EGLint)w,EGL_HEIGHT,(EGLint)h,EGL_LINUX_DRM_FOURCC_EXT,(EGLint)fourcc,EGL_DMA_BUF_PLANE0_FD_EXT,fd,EGL_DMA_BUF_PLANE0_OFFSET_EXT,0,EGL_DMA_BUF_PLANE0_PITCH_EXT,(EGLint)stride};
    int n=12;
    if(modifier!=UINT64_MAX){
      if(!epoxy_has_egl_extension(display,"EGL_EXT_image_dma_buf_import_modifiers")){close(fd);g_dbus_method_invocation_return_error(invocation,G_IO_ERROR,G_IO_ERROR_NOT_SUPPORTED,"DRM modifier import unavailable");return;}
      attributes[n++]=EGL_DMA_BUF_PLANE0_MODIFIER_LO_EXT;attributes[n++]=(EGLint)modifier;
      attributes[n++]=EGL_DMA_BUF_PLANE0_MODIFIER_HI_EXT;attributes[n++]=(EGLint)(modifier>>32);
    }
    attributes[n]=EGL_NONE;
    EGLImageKHR next=eglCreateImageKHR(display,EGL_NO_CONTEXT,EGL_LINUX_DMA_BUF_EXT,NULL,attributes);close(fd);
    if(next==EGL_NO_IMAGE_KHR){g_dbus_method_invocation_return_error(invocation,G_IO_ERROR,G_IO_ERROR_FAILED,"EGL import failed: 0x%x",eglGetError());return;}
    discard_image();image=next;fb_width=w;fb_height=h;y0_top=top;
    glGenTextures(1,&texture);glBindTexture(GL_TEXTURE_2D,texture);glEGLImageTargetTexture2DOES(GL_TEXTURE_2D,image);
    glTexParameteri(GL_TEXTURE_2D,GL_TEXTURE_MIN_FILTER,GL_LINEAR);glTexParameteri(GL_TEXTURE_2D,GL_TEXTURE_MAG_FILTER,GL_LINEAR);
    glTexParameteri(GL_TEXTURE_2D,GL_TEXTURE_WRAP_S,GL_CLAMP_TO_EDGE);glTexParameteri(GL_TEXTURE_2D,GL_TEXTURE_WRAP_T,GL_CLAMP_TO_EDGE);
    g_message("Imported QEMU DMA-BUF: %ux%u fourcc=%08x modifier=%" G_GINT64_MODIFIER "x y0_top=%d",w,h,fourcc,modifier,top);
    gtk_gl_area_queue_render(GTK_GL_AREA(area));
  } else if(!strcmp(name,"UpdateDMABUF")) {
    acknowledge();update=g_object_ref(invocation);
    if(gtk_widget_get_mapped(area))gtk_gl_area_queue_render(GTK_GL_AREA(area));else acknowledge();
    return;
  } else if(!strcmp(name,"Disable")) { discard_image();gtk_gl_area_queue_render(GTK_GL_AREA(area)); }
  else if(!strcmp(name,"Scanout")||!strcmp(name,"Update")) {
    discard_image();g_warning("QEMU offered CPU pixels; DMA-BUF is required");
    g_dbus_method_invocation_return_error(invocation,G_IO_ERROR,G_IO_ERROR_NOT_SUPPORTED,"DMA-BUF scanout required; CPU pixel transport is disabled");return;
  }
  g_dbus_method_invocation_return_value(invocation,NULL);
}
static GVariant *property(GDBusConnection *connection,const char *sender,const char *path,const char *interface,const char *name,GError **error,gpointer unused) {
  if(!strcmp(name,"Interfaces"))return g_variant_new_strv(NULL,0);
  return NULL;
}
static const GDBusInterfaceVTable vtable={.method_call=method,.get_property=property};
static void disconnected(GDBusConnection *connection,gboolean remote,GError *error,gpointer unused){
  visible=FALSE;gtk_gl_area_queue_render(GTK_GL_AREA(area));g_warning("QEMU display disconnected");
}
GtkWidget *dmabuf_area_new(void){
  area=gtk_gl_area_new();gtk_gl_area_set_required_version(GTK_GL_AREA(area),3,3);gtk_gl_area_set_use_es(GTK_GL_AREA(area),FALSE);
  g_signal_connect(area,"realize",G_CALLBACK(realize),NULL);g_signal_connect(area,"render",G_CALLBACK(render),NULL);g_signal_connect(area,"unrealize",G_CALLBACK(unrealize),NULL);return area;
}
gboolean dmabuf_connect(const char *address,const char *relay_path,GError **error){
  bus=g_dbus_connection_new_for_address_sync(address,G_DBUS_CONNECTION_FLAGS_AUTHENTICATION_CLIENT|G_DBUS_CONNECTION_FLAGS_MESSAGE_BUS_CONNECTION,NULL,NULL,error);if(!bus){g_prefix_error(error,"display bus: ");return FALSE;}
  int pair[2]={-1,-1};
  if(relay_path){
    struct sockaddr_un addr={.sun_family=AF_UNIX};
    if(strlen(relay_path)>=sizeof addr.sun_path){g_set_error_literal(error,G_IO_ERROR,G_IO_ERROR_INVALID_ARGUMENT,"Relay socket path too long");return FALSE;}
    strcpy(addr.sun_path,relay_path);
    pair[0]=socket(AF_UNIX,SOCK_STREAM|SOCK_CLOEXEC,0);
    struct timeval timeout={.tv_sec=10};
    if(pair[0]>=0){setsockopt(pair[0],SOL_SOCKET,SO_RCVTIMEO,&timeout,sizeof timeout);setsockopt(pair[0],SOL_SOCKET,SO_SNDTIMEO,&timeout,sizeof timeout);}
    if(pair[0]<0||connect(pair[0],(struct sockaddr*)&addr,sizeof addr)){if(pair[0]>=0)close(pair[0]);g_set_error_literal(error,G_IO_ERROR,G_IO_ERROR_FAILED,"Cannot connect listener relay");return FALSE;}
    char marker=0,control[CMSG_SPACE(16*sizeof(int))];struct iovec iov={&marker,1};
    struct msghdr message={.msg_iov=&iov,.msg_iovlen=1,.msg_control=control,.msg_controllen=sizeof control};
    ssize_t n=recvmsg(pair[0],&message,MSG_CMSG_CLOEXEC);
    int count=0;
    for(struct cmsghdr *header=CMSG_FIRSTHDR(&message);header;header=CMSG_NXTHDR(&message,header)){
      if(header->cmsg_level!=SOL_SOCKET||header->cmsg_type!=SCM_RIGHTS)continue;
      size_t size=header->cmsg_len-CMSG_LEN(0);
      for(size_t i=0;i+sizeof(int)<=size;i+=sizeof(int)){
        int fd;memcpy(&fd,(char*)CMSG_DATA(header)+i,sizeof fd);
        if(count++==0)pair[1]=fd;else close(fd);
      }
    }
    if(n!=1||marker!='F'||(message.msg_flags&MSG_CTRUNC)||count!=1){if(pair[1]>=0)close(pair[1]);close(pair[0]);g_set_error_literal(error,G_IO_ERROR,G_IO_ERROR_INVALID_DATA,"Invalid relay descriptor");return FALSE;}
  }else{
    if(socketpair(AF_UNIX,SOCK_STREAM|SOCK_CLOEXEC,0,pair)){g_set_error_literal(error,G_IO_ERROR,G_IO_ERROR_FAILED,"socketpair failed");return FALSE;}
    int passcred=1;
    if(setsockopt(pair[0],SOL_SOCKET,SO_PASSCRED,&passcred,sizeof passcred)||setsockopt(pair[1],SOL_SOCKET,SO_PASSCRED,&passcred,sizeof passcred)){close(pair[0]);close(pair[1]);g_set_error_literal(error,G_IO_ERROR,G_IO_ERROR_FAILED,"SO_PASSCRED failed");return FALSE;}
  }
  GUnixFDList *fds=g_unix_fd_list_new();int index=g_unix_fd_list_append(fds,pair[1],error);close(pair[1]);if(index<0){close(pair[0]);g_object_unref(fds);return FALSE;}
  GVariant *reply=g_dbus_connection_call_with_unix_fd_list_sync(bus,"org.qemu",CONSOLE,"org.qemu.Display1.Console","RegisterListener",g_variant_new("(h)",index),NULL,G_DBUS_CALL_FLAGS_NONE,10000,fds,NULL,NULL,error);g_object_unref(fds);
  if(!reply){close(pair[0]);return FALSE;}g_variant_unref(reply);
  GSocket *socket=g_socket_new_from_fd(pair[0],error);if(!socket)return FALSE;
  g_socket_set_timeout(socket,10);
  GSocketConnection *stream=g_socket_connection_factory_create_connection(socket);g_object_unref(socket);
  peer=g_dbus_connection_new_sync(G_IO_STREAM(stream),NULL,G_DBUS_CONNECTION_FLAGS_AUTHENTICATION_CLIENT|G_DBUS_CONNECTION_FLAGS_DELAY_MESSAGE_PROCESSING,NULL,NULL,error);g_socket_set_timeout(g_socket_connection_get_socket(stream),0);g_object_unref(stream);if(!peer){g_prefix_error(error,"listener peer: ");return FALSE;}
  schema=g_dbus_node_info_new_for_xml(listener_xml,error);if(!schema)return FALSE;
  if(!g_dbus_connection_register_object(peer,LISTENER,schema->interfaces[0],&vtable,NULL,NULL,error))return FALSE;
  g_signal_connect(peer,"closed",G_CALLBACK(disconnected),NULL);g_signal_connect(bus,"closed",G_CALLBACK(disconnected),NULL);
  g_dbus_connection_start_message_processing(peer);return TRUE;
}
void dmabuf_crop(int x,int y,int w,int h,gboolean show){crop_x=x;crop_y=y;crop_width=w;crop_height=h;visible=show&&dmabuf_ready();gtk_gl_area_queue_render(GTK_GL_AREA(area));}
void dmabuf_size(int *w,int *h){*w=fb_width;*h=fb_height;}
gboolean dmabuf_ready(void){return texture&&bus&&peer&&!g_dbus_connection_is_closed(bus)&&!g_dbus_connection_is_closed(peer);}
static void call_done(GObject *source,GAsyncResult *result,gpointer unused){GError *error=NULL;GVariant *reply=g_dbus_connection_call_finish(G_DBUS_CONNECTION(source),result,&error);if(reply)g_variant_unref(reply);if(error){g_warning("QEMU input/control: %s",error->message);g_error_free(error);}}
void dmabuf_input(const char *interface,const char *name,GVariant *args){if(!bus)return;char *iface=g_strconcat("org.qemu.Display1.",interface,NULL);g_dbus_connection_call(bus,"org.qemu",CONSOLE,iface,name,args,NULL,G_DBUS_CALL_FLAGS_NONE,3000,NULL,call_done,NULL);g_free(iface);}
void dmabuf_ui_size(int w,int h){dmabuf_input("Console","SetUIInfo",g_variant_new("(qqiiuu)",0,0,0,0,(guint)w,(guint)h));}
void dmabuf_disconnect(void){if(peer)g_dbus_connection_close_sync(peer,NULL,NULL);if(bus)g_dbus_connection_close_sync(bus,NULL,NULL);}
