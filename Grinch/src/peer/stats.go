package main

import (
  "sync"
  "time"
  "log"
  "encoding/binary"
)

const (
	BYTE = 1.0 << (10 * iota)
	KILOBYTE
	MEGABYTE
	GIGABYTE
	TERABYTE
)

type Packet struct {
  // first 8 bytes is Session ID
  payload []byte
}

func (p *Packet) Size() int {
  return len(p.payload)
}

type PacketBenchmark struct {
  sentPackets chan *Packet
  receivedBuffers chan []byte
  statsMap map[uint64]time.Time
  mutex *sync.Mutex
  sentStatsChan chan int
  receivedStatsChan chan int
  rttChan chan time.Duration
  lostPacketsChan chan int
}

func (pb *PacketBenchmark) generateData(dataChan chan<- *Packet, size int) {
  log.Println("Generating millions of data...")

  if size < 20 {
    panic("Packet size cannot be smaller than 20 bytes")
  }

  index := uint64(0)
  
  for {
    buffer := make([]byte, size)

    index++

    binary.BigEndian.PutUint64(buffer[SID_SIZE:], index)
    
    for i := (SID_SIZE + 8); i < size; i++ {
      buffer[i] = byte(i % 256);
    }

    dataChan <- &Packet{ payload: buffer }
  }
}

func (pb *PacketBenchmark) accountSent(data []byte) {
  n := len(data)
  t := time.Now()
  //atomic.AddUint64(&c.sentBytes, uint64(n))
  pb.sentStatsChan <- n

  index := binary.BigEndian.Uint64(data[SID_SIZE:(SID_SIZE+8)])
  
  go pb.accountRtt(index, t)
  go pb.checkLost(index)
}

func (pb *PacketBenchmark) accountReceive(data []byte) {
  n := len(data)
  t := time.Now();
  //atomic.AddUint64(&c.receivedBytes, uint64(n))
  pb.receivedStatsChan <- n

  index := binary.BigEndian.Uint64(data[SID_SIZE:(SID_SIZE+8)])
  pb.accountRtt(index, t)
}

func (pb *PacketBenchmark) accountRtt(index uint64, last time.Time) {
  pb.mutex.Lock();
  
  if first, ok := pb.statsMap[index]; ok {
    var elapsed time.Duration
    if first.Before(last) {
      elapsed = last.Sub(first)
    } else {
      elapsed = first.Sub(last)
    }
    
    delete(pb.statsMap, index)
    go func(d time.Duration) {
      pb.rttChan <- elapsed
    }(elapsed)
  } else {      
    pb.statsMap[index] = last
  }

  pb.mutex.Unlock();
}

func (pb *PacketBenchmark) calculateStats(updateFrequency int) {
  log.Println("Handling stats...")

  rttSum, rttCount := uint64(0), uint64(0)
  sent, received := uint64(0), uint64(0)
  sentTotalCount, sentCount := uint64(0), uint64(0)
  receivedVerifiedCount, receivedCount, rttCount := uint64(0), uint64(0), uint64(0)
  tick := time.Tick(time.Duration(updateFrequency) * time.Second)

  lost := uint64(0)
  start := time.Now()

  for {
    select {
    case <- tick:
      log.Println()
      
      up := float64(sent) * 8.0 / float64(MEGABYTE) / float64(updateFrequency)
      down := float64(received) * 8.0 / float64(MEGABYTE) / float64(updateFrequency)
      log.Printf("Client speed >>   UP: %.2f Mbps     DOWN: %.2f Mbps", up, down)
      //log.Printf("Packets sent TOTAL >>   UP: %v     DOWN: %v", sentCount, receivedCount)
      log.Printf("Recieved packets   verified: %v    total: %v", receivedVerifiedCount, receivedCount)
      log.Printf("Packets lost %v out of %v sent during the last %.2f seconds (%.2f %%)", lost, sentCount, time.Since(start).Seconds(), (float64(lost)*100.0/float64(sentTotalCount)))

      averageRtt := 0.0
      if rttCount > 0 {
        averageRtt = float64(rttSum)/float64(rttCount)
      }
      log.Printf("Average RTT over last ~ %v seconds is: %.2f ms for %v verified packets", updateFrequency, averageRtt, rttCount)

      rttSum, rttCount = 0, 0
      sent, received = 0, 0
      //sentCount, receivedCount = 0, 0
    case s := <-pb.sentStatsChan:
      sent += uint64(s)
      sentTotalCount++
      sentCount++
    case r := <-pb.receivedStatsChan:
      received += uint64(r)
      receivedCount++
    case l := <-pb.lostPacketsChan:
      lost += uint64(l)
    case rtt := <-pb.rttChan:
      // time.Duration is nanoseconds
      rttSum += uint64(rtt / time.Millisecond)
      rttCount++
      receivedVerifiedCount++
    }
  }
}

func (pb *PacketBenchmark) checkLost(index uint64) {
  time.Sleep(10*time.Second)
  
  pb.mutex.Lock()
  
  if _, ok := pb.statsMap[index]; ok {
    pb.lostPacketsChan <- 1
  }

  pb.mutex.Unlock()
}
