package main

import (
  "net"
  "log"
  "time"
  "encoding/binary"
)

const (
  allocPort = ":3478"
  payloadPort = ":3479"
)

type Client struct {
  sid uint64
  rate int
  burstLimit int
  // sentBytes uint64
  // receivedBytes uint64
  dataChan <-chan *Packet
  benchmark *PacketBenchmark
}

func (c *Client) talkToServer(relayIP string) {
  err := c.allocateSession(relayIP)
  if err == nil {
    tick := time.NewTicker(time.Second / time.Duration(c.rate))
    defer tick.Stop()
    throttle := make(chan time.Time, c.burstLimit)
    go func() {
      for t := range tick.C {
        select {
        case throttle <- t:
        default:
        }
      }  // does not exit after tick.Stop()
    }()
    
    c.sendData(relayIP, throttle)
  }
}

func (c *Client) allocateSession(relayIP string) error {
  allocAddr, err := net.ResolveUDPAddr("udp", relayIP + allocPort)
  if err != nil {
    log.Println("Failed to resolve server address for allocation")
    return err
  }

  conn, err := net.DialUDP("udp", nil, allocAddr)
  if err != nil {
    log.Printf("Failed to connect to allocate endpoint %v", allocAddr)
    return err
  }

  defer conn.Close()

  err = binary.Write(conn, binary.BigEndian, c.sid)
  if err != nil {
    log.Println("Failed to send session id")
    return err
  }

  go c.receiveData(conn.LocalAddr().(*net.UDPAddr))

  return nil
}

func (c *Client) sendData(relayIP string, throttle <-chan time.Time) {
  var err error
  
  payloadAddr, err := net.ResolveUDPAddr("udp", relayIP + payloadPort)
  if err != nil {
    log.Println("Failed to resolve server address for sending")
    return
  }

  conn, err := net.DialUDP("udp", nil, payloadAddr)
  if err != nil {
    log.Printf("Failed to connect to payload endpoint %v", payloadAddr)
    return
  }

  defer conn.Close()

  var n int
  // log.Printf("Starting sending data within session %v", c.sid)

  for err == nil {
    <- throttle
    packet := <- c.dataChan

    binary.BigEndian.PutUint64(packet.payload[0:SID_SIZE], c.sid)
    n, err = conn.Write(packet.payload)
    if err == nil {
      go c.benchmark.accountSent(packet.payload[0:n])
    }
  }

  log.Printf("Failed to send data to server:", err)
}

func (c *Client) receiveData(addr *net.UDPAddr) {
  //log.Printf("Listening at %v...", addr)
  connection, err := net.ListenUDP("udp", addr)
  if err != nil {
    log.Printf("Failed to listen at %v: %v", addr, err)
    return
  }

  var buf [2048]byte
  n, addr := 0, new(net.UDPAddr)

  for err == nil {
    n, addr, err = connection.ReadFromUDP(buf[0:])
    if err == nil {
      go c.benchmark.accountReceive(buf[0:n])
    }
  }

  log.Printf("Failed to receive data from server:", err)
}
