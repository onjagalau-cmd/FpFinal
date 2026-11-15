#include "dqnative.h"

#define DQ_MAX_PRICE ((INT64_MAX - 5000) / 10000)

int64_t dq_surcharge(int64_t price_units, int32_t basis_points)
{
    if (price_units < 0 || price_units > DQ_MAX_PRICE)
        return -1;
    if (basis_points < 0 || basis_points > 10000)
        return -1;

    return (price_units * (int64_t)basis_points + 5000) / 10000;
}
