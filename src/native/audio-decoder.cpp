#include "aacdecoder_lib.h"
#include <string.h>

// Raw MPEG-4 AAC access units from the negotiated AirPlay RTP stream.
extern "C" __declspec(dllexport) HANDLE_AACDECODER isx_aac_open(int codec) {
    unsigned char eld[] = {0xf8,0xe8,0x50,0x00}, lc[] = {0x12,0x10};
    unsigned char *config = codec == 8 ? eld : lc;
    unsigned int size = codec == 8 ? sizeof(eld) : sizeof(lc);
    HANDLE_AACDECODER decoder = aacDecoder_Open(TT_MP4_RAW, 1);
    if (decoder && aacDecoder_ConfigRaw(decoder, &config, &size) != AAC_DEC_OK) {
        aacDecoder_Close(decoder);
        return 0;
    }
    return decoder;
}
extern "C" __declspec(dllexport) int isx_aac_frame(HANDLE_AACDECODER decoder,
        unsigned char *data, unsigned int size, short *pcm, int capacity) {
    unsigned int remaining = size;
    if (size && (aacDecoder_Fill(decoder, &data, &size, &remaining) != AAC_DEC_OK || remaining)) return 0;
    AAC_DECODER_ERROR result = aacDecoder_DecodeFrame(decoder, pcm, capacity, size ? 0 : AACDEC_CONCEAL);
    if (!IS_OUTPUT_VALID(result)) return 0;
    CStreamInfo *info = aacDecoder_GetStreamInfo(decoder);
    if (info->numChannels != 2 || info->sampleRate != 44100 || info->frameSize > capacity/2) return 0;
    return info->frameSize * 2 * sizeof(short);
}
extern "C" __declspec(dllexport) void isx_aac_close(HANDLE_AACDECODER decoder) {
    aacDecoder_Close(decoder);
}
