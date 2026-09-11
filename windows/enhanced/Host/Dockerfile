# QEMU's existing D-Bus/OpenGL backend; no SPICE or QEMU source patch.
ARG BASE_IMAGE=omarchy-windows-gtk
FROM ${BASE_IMAGE}
USER root
RUN apt-get update \
 && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
      libegl-mesa0 libgl1-mesa-dri libgbm1 python3 \
 && rm -rf /var/lib/apt/lists/*
COPY listener-relay.py container-entry.py /opt/omarchy/
ENTRYPOINT ["/usr/bin/tini", "-s", "/usr/bin/python3", "/opt/omarchy/container-entry.py"]
