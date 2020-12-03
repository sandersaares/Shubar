package main

import (
  "log"
  "time"
)

type Stats struct {
  sent int
  received int
}

const (
	BYTE = 1.0 << (10 * iota)
	KILOBYTE
	MEGABYTE
	GIGABYTE
	TERABYTE
)

func calculateStats(sentStatsChan <-chan int, receivedStatsChan <-chan int) {
  log.Println("Handling stats...")

  const seconds = 5
  
  sent, received := uint64(0), uint64(0)
  tick := time.Tick(seconds * time.Second)

  for {
    select {
    case <- tick:
      up := float64(sent) * 8.0 / float64(MEGABYTE) / float64(seconds)
      down := float64(received) * 8.0 / float64(MEGABYTE) / float64(seconds)

      log.Printf("Server speed >>   UP: %.2f Mbps    DOWN: %.2f Mbps", up, down)
      sent, received = 0, 0
    case s := <-sentStatsChan:
      sent += uint64(s)
    case r := <-receivedStatsChan:
      received += uint64(r)
    }
  }
}
