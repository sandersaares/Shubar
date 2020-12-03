package main

import (
  "net"
)

type Session struct {
  addr *net.UDPAddr
}
