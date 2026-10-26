package main

import (
 "bufio"
 "encoding/json"
 "errors"
 "flag"
 "fmt"
 "io"
 "os"
 "sync/atomic"
 "time"

 "github.com/pion/webrtc/v4"
)

// One process owns one DTLS peer and one verified private file; stdout is SDP only.
func run() error {
 source := flag.String("source", "", "verified private file")
 checksum := flag.String("checksum", "", "verified SHA-256")
 stun := flag.String("stun", "stun:stun.cloudflare.com:3478", "STUN discovery, never TURN")
 flag.Parse()
 file, err := os.Open(*source)
 if err != nil { return err }
 defer file.Close()
 stat, err := file.Stat()
 if err != nil { return err }
 var offer webrtc.SessionDescription
 if err = json.NewDecoder(io.LimitReader(bufio.NewReader(os.Stdin), 65536)).Decode(&offer); err != nil || offer.Type != webrtc.SDPTypeOffer { return errors.New("invalid offer") }
 config := webrtc.Configuration{}
 if *stun != "" { config.ICEServers = []webrtc.ICEServer{{URLs: []string{*stun}}} }
 peer, err := webrtc.NewPeerConnection(config)
 if err != nil { return err }
 defer peer.Close()
 done := make(chan error, 1)
 var accepted atomic.Bool
 opened := make(chan struct{})
 peer.OnDataChannel(func(channel *webrtc.DataChannel) {
  if !accepted.CompareAndSwap(false, true) || channel.Label() != "backup-download-v1" || !channel.Ordered() || channel.MaxRetransmits() != nil || channel.MaxPacketLifeTime() != nil { channel.Close(); return }
  acks := make(chan string, 1)
  channel.OnMessage(func(message webrtc.DataChannelMessage) {
   if message.IsString && len(message.Data) <= 32 { select { case acks <- string(message.Data): default: } }
  })
  channel.OnClose(func() { select { case done <- errors.New("peer closed"): default: } })
  channel.OnOpen(func() {
   close(opened)
   go func() {
    wait := func(expected string) error {
     select {
     case ack := <-acks: if ack != expected { return errors.New("invalid disk acknowledgement") }; return nil
     case <-time.After(45 * time.Second): return errors.New("disk acknowledgement timeout")
     }
    }
    send := func() error {
     header, _ := json.Marshal(struct { SizeBytes int64 `json:"sizeBytes"`; Checksum string `json:"checksum"` }{stat.Size(), *checksum})
     if err := channel.SendText(string(header)); err != nil { return err }
     if err := wait("ready"); err != nil { return err }
     buffer := make([]byte, 32 * 1024)
     for {
      count, err := file.Read(buffer)
      if count > 0 {
       if err := channel.Send(buffer[:count]); err != nil { return err }
       if err := wait("ack"); err != nil { return err }
      }
      if err == io.EOF { break }
      if err != nil { return err }
     }
     if err := channel.SendText("done"); err != nil { return err }
     return wait("saved")
    }
    select { case done <- send(): default: }
   }()
  })
 })
 if err = peer.SetRemoteDescription(offer); err != nil { return err }
 answer, err := peer.CreateAnswer(nil)
 if err != nil { return err }
 gathered := webrtc.GatheringCompletePromise(peer)
 if err = peer.SetLocalDescription(answer); err != nil { return err }
 select { case <-gathered: case <-time.After(15 * time.Second): return errors.New("ICE gathering timeout") }
 if err = json.NewEncoder(os.Stdout).Encode(peer.LocalDescription()); err != nil { return err }
 // No public listener or relay: unsupported NAT/firewall paths fail closed.
 select { case <-opened: case err := <-done: return err; case <-time.After(45 * time.Second): return errors.New("direct connection timeout") }
 select { case err := <-done: return err; case <-time.After(15 * time.Minute): return errors.New("session timeout") }
}

func main() {
 if err := run(); err != nil { fmt.Fprintln(os.Stderr, "DirectPathUnavailable"); os.Exit(1) }
}
