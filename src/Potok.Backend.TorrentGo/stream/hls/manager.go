package hls

import (
	"context"
	"log/slog"
	"sync"
	"time"

	"golang.org/x/sync/singleflight"
)

type SessionManager struct {
	mu       sync.RWMutex
	sessions map[string]*HLSSession
	sfg      singleflight.Group
}

func NewSessionManager() *SessionManager {
	return &SessionManager{
		sessions: make(map[string]*HLSSession),
	}
}

func (m *SessionManager) GetOrCreate(key string, spawnFn func() (*HLSSession, error)) (*HLSSession, error) {
	m.mu.RLock()
	if s, ok := m.sessions[key]; ok {
		if !s.HasFailed() {
			s.Touch()
			m.mu.RUnlock()
			return s, nil
		}
	}
	m.mu.RUnlock()

	res, err, _ := m.sfg.Do(key, func() (interface{}, error) {
		m.mu.Lock()
		if s, ok := m.sessions[key]; ok {
			if !s.HasFailed() {
				s.Touch()
				m.mu.Unlock()
				return s, nil
			}
			slog.Info("Replacing failed HLS session", "key", key)
			s.Close()
			delete(m.sessions, key)
		}
		m.mu.Unlock()

		s, err := spawnFn()
		if err != nil {
			return nil, err
		}

		m.mu.Lock()
		m.sessions[key] = s
		m.mu.Unlock()

		return s, nil
	})

	if err != nil {
		return nil, err
	}

	return res.(*HLSSession), nil
}

func (m *SessionManager) Get(key string) (*HLSSession, bool) {
	m.mu.RLock()
	defer m.mu.RUnlock()
	s, ok := m.sessions[key]
	if ok {
		s.Touch()
	}
	return s, ok
}

func (m *SessionManager) Destroy(key string) {
	m.mu.Lock()
	s, ok := m.sessions[key]
	if ok {
		delete(m.sessions, key)
	}
	m.mu.Unlock()

	if ok && s != nil {
		slog.Info("Destroying HLS session", "key", key)
		s.Close()
	}
}

func (m *SessionManager) StartCleaner(ctx context.Context, idleTimeout time.Duration) {
	ticker := time.NewTicker(10 * time.Second)
	defer ticker.Stop()

	for {
		select {
		case <-ctx.Done():
			m.mu.Lock()
			for k, s := range m.sessions {
				s.Close()
				delete(m.sessions, k)
			}
			m.mu.Unlock()
			return
		case <-ticker.C:
			m.mu.Lock()
			now := time.Now()
			for k, s := range m.sessions {
				if now.Sub(s.GetLastAccess()) > idleTimeout {
					slog.Info("HLS session timed out, cleaning up", "key", k, "idleDuration", now.Sub(s.GetLastAccess()))
					s.Close()
					delete(m.sessions, k)
				}
			}
			m.mu.Unlock()
		}
	}
}
