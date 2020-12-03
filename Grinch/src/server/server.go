package main

import (
  "net"
  "log"
  "sync"
  "encoding/binary"
  "runtime"
)

type Server struct {
  allocConn *net.UDPConn
  allocPort int
  payloadPort int
  ip net.IP
  payloadAddress *net.UDPAddr
  sessionMap map[uint64]*Session
  mutex *sync.RWMutex
  sentStatsChan chan<- int
  receivedStatsChan chan<- int
}

func (s *Server) Run() {
  quit := make(chan struct{})
  
  go s.serveAllocRequests(quit)
  go s.servePayloadRequests(quit)

  // wait until message from quit channel
  <- quit
}

func (s *Server) serveAllocRequests(quit chan struct{}) {
  allocAddress := &net.UDPAddr {
    IP: s.ip,
    Port: s.allocPort,
  }
  var err error
  
  s.allocConn, err = net.ListenUDP("udp", allocAddress)
  if err != nil { panic(err) }
  
  for i := 0; i < 2; i++ {
    go s.handleAllocRequests(s.allocConn, quit)
  }

  log.Printf("Listening to ALLOC requests at %v...", allocAddress)
}

func (s *Server) handleAllocRequests(connection *net.UDPConn, quit chan struct{}) {
  var buf [8]byte  
  n, addr, err := 0, new(net.UDPAddr), error(nil)

  for err == nil {
    n, addr, err = connection.ReadFromUDP(buf[0:])
    if err != nil { continue }

    if n < 8 {
      log.Printf("Wrong message format from %v", addr)
      continue
    }

    sid := binary.BigEndian.Uint64(buf[0:8])
    //log.Printf("Allocating Session ID %v for address %v", sid, addr)

    s.mutex.Lock();
    {
      s.sessionMap[sid] = &Session{addr: addr}
    }
    s.mutex.Unlock()
  }

  log.Printf("Alloc listener failed: %v", err)
  quit <- struct{}{}
}

func (s *Server) servePayloadRequests(quit chan struct{}) {
  s.payloadAddress = &net.UDPAddr {
    IP: s.ip,
    Port: s.payloadPort,
  }
  
  connection, err := net.ListenUDP("udp", s.payloadAddress)
  if err != nil { panic(err) }

  for i := 0; i < runtime.NumCPU(); i++ {
    go s.handlePayloadRequests(connection, quit)
  }

  log.Printf("Listening to PAYLOAD requests at %v...", s.payloadAddress)
}

func (s *Server) handlePayloadRequests(connection *net.UDPConn, quit chan struct{}) {
  var buf [2048]byte
  n, addr, err := 0, new(net.UDPAddr), error(nil)

  for err == nil {
    n, addr, err = connection.ReadFromUDP(buf[0:])
    if err != nil { continue }

    go func(r int, statsChan chan<- int) {
      statsChan <- r
    }(n, s.receivedStatsChan)
    
    //log.Printf("Handling PAYLOAD request (%v bytes) from %v", n, addr)

    if n < 8 {
      log.Printf("Wrong message format from %v", addr)
      continue
    }

    sid := binary.BigEndian.Uint64(buf[0:8])
    s.mutex.RLock()
    {
      if session, ok := s.sessionMap[sid]; ok {
        go s.relayPayload(buf[0:n], session)
      } else {
        log.Printf("Unknown SID received: %v", sid)
      }
    }
    s.mutex.RUnlock()
  }

  log.Printf("Payload listener failed: %v", err)
  quit <- struct{}{}
}

func (s *Server) relayPayload(data []byte, session *Session) {
  var err error
  
  n, err := s.allocConn.WriteToUDP(data, session.addr)
  if err == nil {
    go func(k int, statsChan chan<- int) {
      statsChan <- k
    }(n, s.sentStatsChan)
  } else {
    log.Printf("Couldn't send response: %v", err)
  }      
}
