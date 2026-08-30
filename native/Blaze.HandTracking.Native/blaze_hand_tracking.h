#ifndef BLAZE_HAND_TRACKING_H_
#define BLAZE_HAND_TRACKING_H_

#include <stdint.h>

#define BLAZE_HAND_ABI_VERSION 1
#define BLAZE_HAND_LANDMARK_COUNT 21

#if defined(_WIN32)
#define BLAZE_HAND_API __declspec(dllexport)
#define BLAZE_HAND_CALL __cdecl
#else
#define BLAZE_HAND_API __attribute__((visibility("default")))
#define BLAZE_HAND_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef void* BlazeHandHandle;

typedef enum BlazeHandStatus {
  BLAZE_HAND_SUCCESS = 0,
  BLAZE_HAND_INVALID_ARGUMENT = 1,
  BLAZE_HAND_INVALID_STATE = 2,
  BLAZE_HAND_NATIVE_FAILURE = 3,
  BLAZE_HAND_UNKNOWN_FAILURE = 4
} BlazeHandStatus;

typedef struct BlazeHandOptions {
  uint32_t struct_size;
  int32_t max_hands;
  float min_detection_confidence;
  float min_tracking_confidence;
  const char* model_path_utf8;
} BlazeHandOptions;

typedef struct BlazeHandLandmark {
  float x;
  float y;
  float z;
} BlazeHandLandmark;

typedef struct BlazeHandResult {
  float confidence;
  BlazeHandLandmark landmarks[BLAZE_HAND_LANDMARK_COUNT];
} BlazeHandResult;

BLAZE_HAND_API uint32_t BLAZE_HAND_CALL blaze_hand_get_abi_version(void);

BLAZE_HAND_API int32_t BLAZE_HAND_CALL blaze_hand_create(
    const BlazeHandOptions* options,
    BlazeHandHandle* out_handle);

BLAZE_HAND_API int32_t BLAZE_HAND_CALL blaze_hand_process_frame(
    BlazeHandHandle handle,
    const uint8_t* rgb_data,
    int32_t width,
    int32_t height,
    int32_t stride_bytes,
    int64_t timestamp_ms);

BLAZE_HAND_API int32_t BLAZE_HAND_CALL blaze_hand_get_hand_count(
    BlazeHandHandle handle,
    int32_t* out_hand_count);

BLAZE_HAND_API int32_t BLAZE_HAND_CALL blaze_hand_copy_hand(
    BlazeHandHandle handle,
    int32_t hand_index,
    BlazeHandResult* out_result,
    uint32_t result_size);

BLAZE_HAND_API int32_t BLAZE_HAND_CALL blaze_hand_destroy(
    BlazeHandHandle handle);

BLAZE_HAND_API int32_t BLAZE_HAND_CALL blaze_hand_get_last_error(
    BlazeHandHandle handle,
    char* utf8_buffer,
    uint32_t buffer_capacity,
    uint32_t* out_required_size);

#ifdef __cplusplus
}  // extern "C"
#endif

#endif  // BLAZE_HAND_TRACKING_H_
