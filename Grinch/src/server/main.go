package main

import (
  "flag"
  "log"
  "os"
  "sync"
  "fmt"
  "io"
  "net"
  "io/ioutil"
)

const (
  appName = "flamant"
)

// flags
var (
  stdoutFlag = flag.Bool("stdout", false, "Log to stdout and to logfile")
  logPathFlag = flag.String("log", "flamant.log", "Path to the logfile")
  noLogsFlag = flag.Bool("nologs", false, "Discard logs")
  ipFlag = flag.String("ip", "0.0.0.0", "IP address to listen")
  helpFlag = flag.Bool("help", false, "Show help")
)

func main() {
  flag.Parse()
  if *helpFlag {
    flag.Usage()
    os.Exit(0)
  }
  
  logfile, err := setupLogging()
  if err != nil { defer logfile.Close() }

  sentStatsChan := make(chan int, 10000)
  receivedStatsChan := make(chan int, 10000)
  go calculateStats(sentStatsChan, receivedStatsChan)

  server := &Server{
    allocPort: 3478,
    payloadPort: 3479,
    ip: net.ParseIP(*ipFlag),
    sessionMap: make(map[uint64]*Session),
    mutex: new(sync.RWMutex),
    sentStatsChan: sentStatsChan,
    receivedStatsChan: receivedStatsChan,
  }

  server.Run()
}

func setupLogging() (f *os.File, err error) {
  if *noLogsFlag {
    log.SetOutput(ioutil.Discard)
    return nil, nil
  }
  
  f, err = os.OpenFile(*logPathFlag, os.O_RDWR | os.O_CREATE | os.O_APPEND, 0666)
  if err != nil {
    fmt.Println("error opening file: %v", *logPathFlag)
    return nil, err
  }

  if *stdoutFlag {
    mw := io.MultiWriter(os.Stdout, f)
    log.SetOutput(mw)
  } else {
    log.SetOutput(f)
  }

  log.Println("------------------------------")
  log.Println(appName + " log started")

  return f, err
}
