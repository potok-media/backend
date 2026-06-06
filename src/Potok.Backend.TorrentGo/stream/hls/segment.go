package hls

import (
	"sync"
)

type Segment struct {
	Index int
	Name  string
	Data  []byte
	Ready chan struct{}
	once  sync.Once
}

func NewSegment(index int, name string) *Segment {
	return &Segment{
		Index: index,
		Name:  name,
		Ready: make(chan struct{}),
	}
}

func (s *Segment) SetData(data []byte) {
	s.Data = data
	s.once.Do(func() {
		close(s.Ready)
	})
}

func (s *Segment) IsComplete() bool {
	select {
	case <-s.Ready:
		return true
	default:
		return false
	}
}
