package main

import (
  "flag"
  "log"
  "fmt"
  "math/rand"
  "time"
  "os"
  "io"
  "io/ioutil"
  "net"
  "errors"
  "sync"
)

const (
  appName = "grinch"
  payloadMaxSize = 1400
)

// flags
var (
  stdoutFlag = flag.Bool("stdout", false, "Log to stdout and to logfile")
  logPathFlag = flag.String("log", "grinch.log", "Path to the logfile")
  noLogsFlag = flag.Bool("nologs", false, "Discard logs")
  relayIPFlag = flag.String("relay", "127.0.0.1", "Relay IP address")
  bitrateFlag = flag.Int("kbps", 100, "Bitrate in Kbps to send per session")
  sessionNumberFlag = flag.Int("sessions", 100, "Number of sessions to allocate")
  packetSizeFlag = flag.Int("bytes", 1400, "Packet size to send")
  helpFlag = flag.Bool("help", false, "Show help")
  updateFrequencyFlag = flag.Int("update", 4, "Speed update frequency")
)

func main() {
  err := parseFlags()
  if err != nil {
    flag.PrintDefaults()
    log.Fatal(err.Error())
  }
  
  logfile, err := setupLogging()
  if err != nil { defer logfile.Close() }

  log.Printf("Talking to server %v", *relayIPFlag)
  log.Printf("Allocating %v sessions...", *sessionNumberFlag)

  packetsNumber, payloadSize := estimateWorkload()

  rand.Seed(time.Now().UTC().UnixNano())
  clients := make([]*Client, *sessionNumberFlag)
  dataChan := make(chan *Packet, 10000)

  benchmark := &PacketBenchmark{
    sentPackets: make(chan *Packet),
    statsMap: make(map[uint64]time.Time),
    sentStatsChan: make(chan int, (*sessionNumberFlag)*packetsNumber),
    receivedStatsChan: make(chan int, (*sessionNumberFlag)*packetsNumber),
    mutex: new(sync.Mutex),
    rttChan: make(chan time.Duration),
    lostPacketsChan: make(chan int),
  }

  go benchmark.generateData(dataChan, payloadSize)
  go benchmark.calculateStats(*updateFrequencyFlag)

  for i := range clients {
    c := &Client{
      sid: uint64(rand.Int63()),
      dataChan: dataChan,
      burstLimit: packetsNumber,
      rate: packetsNumber,
      benchmark: benchmark,
    }

    clients[i] = c
    
    go c.talkToServer(*relayIPFlag)
  }

  // wait forever
  select {}
}

func estimateWorkload() (int, int) {
  clientWorkload := float64(*bitrateFlag)*1024.0/8.0
  log.Printf("Estimated session workload is %v Kbps", *bitrateFlag)

  packetSize := float64(*packetSizeFlag)
  packetsNumber := clientWorkload / packetSize

  const packetDecrease = 128.0

  for (packetsNumber < 1.0) && (packetSize > 64) {
    packetsNumber = clientWorkload / (packetSize - packetDecrease)
    //log.Printf("Estimated packets number: %v, more frequent: %v, smaller size: %v", packetsNumber, moreFrequent, smallerPacket)
    packetSize = packetSize - packetDecrease

  }
  
  if packetsNumber < 1.0 {
    packetsNumber = 1.0
  }

  if packetSize <= packetDecrease {
    packetSize = packetDecrease
  }

  log.Printf("Client will send %v packet(s) of %v bytes per second", int(packetsNumber), int(packetSize))
  
  return int(packetsNumber), int(packetSize)
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

func parseFlags() error {
  flag.Parse()

  if *helpFlag {
    flag.Usage()
    os.Exit(0)
  }

  trial := net.ParseIP(*relayIPFlag)
  if trial.To4() == nil {
    return errors.New("Relay IP is not a valid IPv4 address")
  }

  return nil
}
