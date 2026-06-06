package hls

import (
	"testing"
	"time"
)

func TestHLSSessionBasic(t *testing.T) {
	session := NewHLSSession("test_key", 5)

	if session.Key != "test_key" {
		t.Errorf("Expected key 'test_key', got %s", session.Key)
	}

	// Test segment creation and readiness
	seg := session.GetOrCreateSegmentPlaceholder("segment_00000.ts", 0)
	if seg.Index != 0 {
		t.Errorf("Expected segment index 0, got %d", seg.Index)
	}

	select {
	case <-seg.Ready:
		t.Errorf("Segment ready channel closed before data is set")
	default:
	}

	session.AddSegment("segment_00000.ts", []byte("mpegts data"))

	select {
	case <-seg.Ready:
		// Success
	case <-time.After(100 * time.Millisecond):
		t.Errorf("Segment ready channel did not close after data is set")
	}

	retrieved, found := session.GetSegment("segment_00000.ts")
	if !found {
		t.Errorf("Expected segment to be found")
	}
	if string(retrieved.Data) != "mpegts data" {
		t.Errorf("Expected 'mpegts data', got %s", string(retrieved.Data))
	}
}

func TestHLSSessionCheckAndSeek(t *testing.T) {
	session := NewHLSSession("test_key", 5)

	// No segments yet
	seekNeeded, _, _ := session.CheckAndSeek(0, 6)
	if seekNeeded {
		t.Errorf("Expected seek to be false on empty session")
	}

	// Add a segment at index 0
	session.AddSegment("segment_00000.ts", []byte("data"))

	// Request index 5 (near, max index is 0, 5 <= 0 + 10)
	seekNeeded, _, _ = session.CheckAndSeek(5, 6)
	if seekNeeded {
		t.Errorf("Expected seek to be false for index near maxIdx")
	}

	// Request index 15 (far, 15 > 0 + 10)
	seekNeeded, startParam, startNum := session.CheckAndSeek(15, 6)
	if !seekNeeded {
		t.Errorf("Expected seek to be true for far index 15")
	}
	if startNum != 15 {
		t.Errorf("Expected start number 15, got %d", startNum)
	}
	if startParam != "90.000000" {
		t.Errorf("Expected startParam '90.000000', got %s", startParam)
	}
}

func TestHLSSessionRotation(t *testing.T) {
	// Set MaxSegments to 3
	session := NewHLSSession("test_key", 3)

	// Add segments 0, 1, 2
	session.AddSegment("segment_00000.ts", []byte("data0"))
	session.AddSegment("segment_00001.ts", []byte("data1"))
	session.AddSegment("segment_00002.ts", []byte("data2"))

	// Verify all exist
	if len(session.Segments) != 3 {
		t.Errorf("Expected 3 segments, got %d", len(session.Segments))
	}

	// Add segment 15. This triggers rotation
	session.AddSegment("segment_00015.ts", []byte("data15"))

	// Since max index is 15, and v.Index < 15 - 10 (i.e. < 5) should be deleted,
	// segments 0, 1, 2 should be deleted.
	if _, ok := session.GetSegment("segment_00000.ts"); ok {
		t.Errorf("Expected segment 0 to be rotated out")
	}
	if _, ok := session.GetSegment("segment_00001.ts"); ok {
		t.Errorf("Expected segment 1 to be rotated out")
	}
	if _, ok := session.GetSegment("segment_00002.ts"); ok {
		t.Errorf("Expected segment 2 to be rotated out")
	}
	if _, ok := session.GetSegment("segment_00015.ts"); !ok {
		t.Errorf("Expected segment 15 to be retained")
	}
}
