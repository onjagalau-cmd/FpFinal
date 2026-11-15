#ifndef DQNATIVE_H
#define DQNATIVE_H

#include <stdint.h>

#ifdef _WIN32
#define DQ_API __declspec(dllexport)
#else
#define DQ_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* Fuel surcharge in whole currency units, rounded half up.
 *   price_units  : 0 .. 922337203685477 (whole currency units)
 *   basis_points : 0 .. 10000 (1 basis point = 0.01 %, 500 = 5 %)
 * Returns (price_units * basis_points + 5000) / 10000, or -1 if an argument is out of range. */
DQ_API int64_t dq_surcharge(int64_t price_units, int32_t basis_points);

#ifdef __cplusplus
}
#endif

#endif
