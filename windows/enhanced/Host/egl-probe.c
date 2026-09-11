#include <epoxy/egl.h>
#include <epoxy/gl.h>
#include <gbm.h>
#include <fcntl.h>
#include <stdio.h>
#include <unistd.h>
#include <string.h>
int main(int argc, char **argv) {
 if (argc != 2) return 2;
 int fd=open(argv[1],O_RDWR|O_CLOEXEC); if(fd<0){perror("open");return 1;}
 struct gbm_device *gbm=gbm_create_device(fd); if(!gbm)return 1;
 EGLDisplay display=eglGetPlatformDisplayEXT(EGL_PLATFORM_GBM_KHR,gbm,NULL);
 EGLint major,minor;
 if(!eglInitialize(display,&major,&minor)){fprintf(stderr,"eglInitialize: %x\n",eglGetError());return 1;}
 const char *ext=eglQueryString(display,EGL_EXTENSIONS);
 printf("%s: EGL %d.%d, vendor %s\n",argv[1],major,minor,eglQueryString(display,EGL_VENDOR));
 printf("DMA-BUF import: %s; modifiers: %s; export: %s\n",strstr(ext,"EGL_EXT_image_dma_buf_import")?"yes":"no",strstr(ext,"EGL_EXT_image_dma_buf_import_modifiers")?"yes":"no",strstr(ext,"EGL_MESA_image_dma_buf_export")?"yes":"no");
 eglBindAPI(EGL_OPENGL_API);
 EGLContext ctx=eglCreateContext(display,EGL_NO_CONFIG_KHR,EGL_NO_CONTEXT,NULL);
 if(ctx==EGL_NO_CONTEXT||!eglMakeCurrent(display,EGL_NO_SURFACE,EGL_NO_SURFACE,ctx)){fprintf(stderr,"context: %x\n",eglGetError());return 1;}
 printf("GL renderer: %s\n",glGetString(GL_RENDERER));
 GLuint texture;glGenTextures(1,&texture);glBindTexture(GL_TEXTURE_2D,texture);
 glTexImage2D(GL_TEXTURE_2D,0,GL_RGBA8,64,64,0,GL_RGBA,GL_UNSIGNED_BYTE,NULL);
 EGLImage image=eglCreateImage(display,ctx,EGL_GL_TEXTURE_2D,(EGLClientBuffer)(uintptr_t)texture,NULL);
 int fourcc,planes;EGLuint64KHR modifier;
 if(!eglExportDMABUFImageQueryMESA(display,image,&fourcc,&planes,&modifier)){fprintf(stderr,"export query: %x\n",eglGetError());return 1;}
 int fds[4]={-1,-1,-1,-1},strides[4],offsets[4];
 if(planes>4||!eglExportDMABUFImageMESA(display,image,fds,strides,offsets)){fprintf(stderr,"export: %x\n",eglGetError());return 1;}
 printf("Exported texture: fourcc=%08x planes=%d modifier=%llx stride=%d offset=%d\n",fourcc,planes,(unsigned long long)modifier,strides[0],offsets[0]);
 for(int i=0;i<planes;i++)if(fds[i]>=0)close(fds[i]);
 eglDestroyImage(display,image);glDeleteTextures(1,&texture);eglMakeCurrent(display,EGL_NO_SURFACE,EGL_NO_SURFACE,EGL_NO_CONTEXT);eglDestroyContext(display,ctx);eglTerminate(display);gbm_device_destroy(gbm);close(fd);return 0;
}
