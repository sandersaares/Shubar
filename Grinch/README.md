# Grinch

This is a naive implementation of packet flipper client that allocates sessions on server, sends data and validates data upon receiving

### Build

You have to install Go for your system from [official website](https://golang.org/doc/install)
Open console and navigate to the root directory of grinch

    cd peer
	go build -o grinch.exe
	
If you want to build simple server for testing the client execute

    cd server
	go build -o flamant.exe
	
### Basic usage

If you want to test client (peer) and server written in Go, the simpliest way to do it is to launch 2 console windows and execute 

    flamant.exe -stdout
	
in one window and 

    grinch.exe -stdout
	
in another window.

Full list of command line arguments can be obtained using `-help` flag passed to either executable.

### Advanced usage

In order to use client agains "production" packet flipper you might want to leverage fine-tuning options like

    grinch -relay "10.164.15.144" -kbps 200 -sessions 4000 -stdout
	
Where the full list of available options is:

	  -bytes int
			Packet size to send (default 1400)
	  -help
			Show help
	  -kbps int
			Bitrate in Kbps to send per session (default 100)
	  -log string
			Path to the logfile (default "grinch.log")
	  -nologs
			Discard logs
	  -relay string
			Relay IP address (default "127.0.0.1")
	  -sessions int
			Number of sessions to allocate (default 100)
	  -stdout
			Log to stdout and to logfile
	  -update int
			Speed update frequency (default 4)