package media

import (
	"errors"
	"fmt"

	"github.com/asticode/go-astiav"
)

// videoEncoder transcodes one incompatible source video stream (HEVC/VP9/AV1/10-bit/…) to browser-playable
// H.264, in-process: decode → convert to 8-bit yuv420p when needed → re-encode. It only encodes decoded
// frames whose pts falls in [startTS, endTS): frames before startTS are pre-roll (needed to decode the
// segment's first real frame when the segment boundary isn't a keyframe — the transcode/uniform grid) and
// frames at/after endTS belong to the next segment. A fresh encoder per segment ⇒ the first emitted frame
// is an IDR keyframe, so each segment is independently decodable; timestamps stay absolute (R6).
type videoEncoder struct {
	ofc    *astiav.FormatContext
	outIdx int

	dec    *astiav.CodecContext
	enc    *astiav.CodecContext
	sws    *astiav.SoftwareScaleContext
	decFrm *astiav.Frame
	scaled *astiav.Frame
	pkt    *astiav.Packet

	startTS int64
	endTS   int64
}

// newVideoEncoder wires the decode→(scale)→H.264-encode pipeline for input stream srcIdx and adds the
// H.264 output stream to ofc (before WriteHeader). Caller must free() it.
func newVideoEncoder(ifc, ofc *astiav.FormatContext, srcIdx int, startTS, endTS int64) (*videoEncoder, error) {
	in := ifc.Streams()[srcIdx]

	decCodec := astiav.FindDecoder(in.CodecParameters().CodecID())
	if decCodec == nil {
		return nil, fmt.Errorf("media: no video decoder for %s", in.CodecParameters().CodecID().String())
	}
	dec := astiav.AllocCodecContext(decCodec)
	if dec == nil {
		return nil, fmt.Errorf("media: alloc video decoder")
	}
	if err := in.CodecParameters().ToCodecContext(dec); err != nil {
		dec.Free()
		return nil, fmt.Errorf("media: video decoder params: %w", err)
	}
	if err := dec.Open(decCodec, nil); err != nil {
		dec.Free()
		return nil, fmt.Errorf("media: open video decoder: %w", err)
	}

	encCodec := astiav.FindEncoder(astiav.CodecIDH264)
	if encCodec == nil {
		dec.Free()
		return nil, fmt.Errorf("media: no H.264 encoder available (ffmpeg built without libx264?)")
	}
	enc := astiav.AllocCodecContext(encCodec)
	if enc == nil {
		dec.Free()
		return nil, fmt.Errorf("media: alloc H.264 encoder")
	}
	enc.SetWidth(dec.Width())
	enc.SetHeight(dec.Height())
	enc.SetPixelFormat(astiav.PixelFormatYuv420P)
	enc.SetSampleAspectRatio(dec.SampleAspectRatio())
	enc.SetTimeBase(in.TimeBase())
	if ofc.OutputFormat().Flags().Has(astiav.IOFormatFlagGlobalheader) {
		enc.SetFlags(enc.Flags().Add(astiav.CodecContextFlagGlobalHeader))
	}
	opts := astiav.NewDictionary()
	defer opts.Free()
	opts.Set("preset", "veryfast", 0)
	opts.Set("crf", "23", 0)
	if err := enc.Open(encCodec, opts); err != nil {
		dec.Free()
		enc.Free()
		return nil, fmt.Errorf("media: open H.264 encoder: %w", err)
	}

	out := ofc.NewStream(nil)
	if out == nil {
		dec.Free()
		enc.Free()
		return nil, fmt.Errorf("media: new video output stream")
	}
	if err := out.CodecParameters().FromCodecContext(enc); err != nil {
		dec.Free()
		enc.Free()
		return nil, fmt.Errorf("media: video encoder params -> stream: %w", err)
	}
	out.SetTimeBase(enc.TimeBase())

	return &videoEncoder{
		ofc:     ofc,
		outIdx:  out.Index(),
		dec:     dec,
		enc:     enc,
		decFrm:  astiav.AllocFrame(),
		scaled:  astiav.AllocFrame(),
		pkt:     astiav.AllocPacket(),
		startTS: startTS,
		endTS:   endTS,
	}, nil
}

func (v *videoEncoder) free() {
	v.pkt.Free()
	v.scaled.Free()
	v.decFrm.Free()
	if v.sws != nil {
		v.sws.Free()
	}
	v.enc.Free()
	v.dec.Free()
}

// feed decodes one source video packet and encodes the resulting in-window frames.
func (v *videoEncoder) feed(pkt *astiav.Packet) error {
	if err := v.dec.SendPacket(pkt); err != nil {
		return fmt.Errorf("media: video send packet: %w", err)
	}
	return v.drainDecoder()
}

func (v *videoEncoder) drainDecoder() error {
	for {
		err := v.dec.ReceiveFrame(v.decFrm)
		if errors.Is(err, astiav.ErrEof) || errors.Is(err, astiav.ErrEagain) {
			return nil
		}
		if err != nil {
			return fmt.Errorf("media: video receive frame: %w", err)
		}
		pts := v.decFrm.Pts()
		if pts != noPTS && (pts < v.startTS || pts >= v.endTS) {
			v.decFrm.Unref() // out of segment window (pre-roll or next-segment spill)
			continue
		}
		if err := v.encodeDecoded(); err != nil {
			v.decFrm.Unref()
			return err
		}
		v.decFrm.Unref()
	}
}

// encodeDecoded converts the current decoded frame to 8-bit yuv420p if needed, then encodes it.
func (v *videoEncoder) encodeDecoded() error {
	frame := v.decFrm
	if v.decFrm.PixelFormat() != astiav.PixelFormatYuv420P {
		if v.sws == nil {
			var err error
			v.sws, err = astiav.CreateSoftwareScaleContext(
				v.decFrm.Width(), v.decFrm.Height(), v.decFrm.PixelFormat(),
				v.decFrm.Width(), v.decFrm.Height(), astiav.PixelFormatYuv420P,
				astiav.SoftwareScaleContextFlags(astiav.SoftwareScaleContextFlagBilinear),
			)
			if err != nil {
				return fmt.Errorf("media: create sws: %w", err)
			}
		}
		v.scaled.Unref()
		v.scaled.SetWidth(v.decFrm.Width())
		v.scaled.SetHeight(v.decFrm.Height())
		v.scaled.SetPixelFormat(astiav.PixelFormatYuv420P)
		if err := v.sws.ScaleFrame(v.decFrm, v.scaled); err != nil {
			return fmt.Errorf("media: scale: %w", err)
		}
		v.scaled.SetPts(v.decFrm.Pts())
		frame = v.scaled
	}
	// Clear the source picture type: a uniform-grid segment starts MID-GOP, so the decoded first frame is
	// a B/P frame. Passing that type to libx264 conflicts with "frame 0 must be an IDR" ("specified frame
	// type … not compatible with keyframe interval") and skews frame typing. NONE lets the encoder decide.
	frame.SetPictureType(astiav.PictureTypeNone)
	return v.encode(frame)
}

// encode sends one frame (nil = flush) to the H.264 encoder and writes every packet it emits.
func (v *videoEncoder) encode(f *astiav.Frame) error {
	if err := v.enc.SendFrame(f); err != nil {
		return fmt.Errorf("media: video send frame: %w", err)
	}
	for {
		err := v.enc.ReceivePacket(v.pkt)
		if errors.Is(err, astiav.ErrEof) || errors.Is(err, astiav.ErrEagain) {
			return nil
		}
		if err != nil {
			return fmt.Errorf("media: video receive packet: %w", err)
		}
		v.pkt.SetStreamIndex(v.outIdx)
		v.pkt.RescaleTs(v.enc.TimeBase(), v.ofc.Streams()[v.outIdx].TimeBase())
		if err := v.ofc.WriteInterleavedFrame(v.pkt); err != nil {
			v.pkt.Unref()
			return fmt.Errorf("media: video write frame: %w", err)
		}
		v.pkt.Unref()
	}
}

// flush drains the decoder then the encoder.
func (v *videoEncoder) flush() error {
	if err := v.dec.SendPacket(nil); err != nil && !errors.Is(err, astiav.ErrEof) {
		return fmt.Errorf("media: flush video decoder: %w", err)
	}
	if err := v.drainDecoder(); err != nil {
		return err
	}
	return v.encode(nil)
}
