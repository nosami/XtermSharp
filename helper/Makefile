all: libpty.dylib

ifeq ($(OS),Windows_NT)
    UNAME := Windows
else
    UNAME := $(shell uname -s)
endif

libpty.dylib: pty.c
	@if [[ "$(UNAME)" == "Darwin" ]]; then \
		cc -mmacosx-version-min=10.14 -arch arm64 -arch x86_64 -shared pty.c -shared -o libpty.dylib; \
	else \
		cc -shared pty.c -shared -o libpty.dylib; \
	fi

clean:
	rm *dylib
