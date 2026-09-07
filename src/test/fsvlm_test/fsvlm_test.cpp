//
// This file is released under the terms of the NASA Open Source Agreement (NOSA)
// version 1.3 as detailed in the LICENSE file which accompanies this software.
//

// fsvlm_test.cpp: regression tests for the free-surface vortex lattice method.
//
//////////////////////////////////////////////////////////////////////
//
// The free-surface percentages below were established with an independent implementation of
// the same formulation, cross-checked against AVL for the unbounded case and against
// published experimental data for the direction of the free-surface effect.  They are
// sensitive to the quadrature, the exponential-integral evaluation and the lattice, so they
// make a good guard against silent numerical regressions.

#include "FreeSurfaceVLM.h"

#include <cmath>
#include <cstdio>

namespace
{

int g_failures = 0;

void Check( const char *what, double got, double expect, double tol )
{
    double err = std::fabs( got - expect );
    bool ok = ( err <= tol );

    printf( "  %-52s got %+9.4f  expect %+9.4f  %s\n",
            what, got, expect, ok ? "ok" : "FAIL" );

    if ( !ok )
    {
        g_failures++;
    }
}

} // namespace

int main()
{
    printf( "Free-surface VLM regression\n\n" );

    // ---- Special functions ------------------------------------------------------------

    printf( "Exponential integral and quadrature:\n" );

    // exp(1) * E1(1)
    Check( "exp(B)E1(B) at B = 1", fsvlm::ExpTimesE1( std::complex < double > ( 1.0, 0.0 ) ).real(),
           0.5963473623231941, 1.0e-12 );

    // The series and asymptotic branches must agree where they meet at |B| = 25.  Sampled
    // in the left half plane, which is the only region the solver evaluates.
    {
        std::complex < double > B = std::polar( 25.0, 3.0 );
        std::complex < double > v = fsvlm::ExpTimesE1( B );
        Check( "exp(B)E1(B) real part at |B| = 25, arg = 3", v.real(), -4.1267832e-02, 1.0e-8 );
        Check( "exp(B)E1(B) imag part at |B| = 25, arg = 3", v.imag(), -6.1549706e-03, 1.0e-8 );
    }

    {
        std::vector < double > x, w;
        fsvlm::GaussLegendre( 40, x, w );

        double i2 = 0.0;
        for ( size_t i = 0; i < x.size(); i++ )
        {
            i2 += w[i] * x[i] * x[i];
        }

        Check( "Gauss-Legendre 40 point integral of x^2", i2, 2.0 / 3.0, 1.0e-13 );
    }

    // ---- Deep submergence: the free-surface term must vanish ---------------------------

    printf( "\nDeep submergence, free-surface influence must vanish:\n" );

    {
        fsvlm::Input off;
        off.submergence = 1000.0;
        off.free_surface = false;

        fsvlm::Input on = off;
        on.free_surface = true;

        fsvlm::Result roff = fsvlm::Solve( off );
        fsvlm::Result ron  = fsvlm::Solve( on );

        Check( "dCL at 1000 m submergence, percent",
               100.0 * ( ron.CL - roff.CL ) / roff.CL, 0.0, 1.0e-6 );
        Check( "dCD at 1000 m submergence, percent",
               100.0 * ( ron.CD - roff.CD ) / roff.CD, 0.0, 1.0e-6 );
    }

    // ---- Design sweep ------------------------------------------------------------------

    printf( "\nSubmergence sweep at alpha = 4 deg, U = 10.29 m/s (20 kt):\n" );

    const double depth[]  = { 20.0,  5.0,   2.0,   1.5,   1.0 };
    const double dCLref[] = { -0.03, -1.41, -6.52, -9.57, -15.19 };
    const double dCDref[] = {  0.06,  3.34, 13.07, 16.83,  19.80 };

    for ( int i = 0; i < 5; i++ )
    {
        fsvlm::Input off;
        off.submergence = depth[i];
        off.free_surface = false;

        fsvlm::Input on = off;
        on.free_surface = true;

        fsvlm::Result roff = fsvlm::Solve( off );
        fsvlm::Result ron  = fsvlm::Solve( on );

        if ( !roff.valid || !ron.valid )
        {
            printf( "  solve failed at depth %.1f\n", depth[i] );
            g_failures++;
            continue;
        }

        char label[128];

        snprintf( label, sizeof( label ), "dCL at %.1f m (h/c %.1f), percent", depth[i], ron.h_over_c );
        Check( label, 100.0 * ( ron.CL - roff.CL ) / roff.CL, dCLref[i], 0.15 );

        snprintf( label, sizeof( label ), "dCD at %.1f m (h/c %.1f), percent", depth[i], ron.h_over_c );
        Check( label, 100.0 * ( ron.CD - roff.CD ) / roff.CD, dCDref[i], 0.15 );
    }

    // ---- The shallow case must warn rather than silently returning a number ------------

    printf( "\nValidity guard:\n" );

    {
        fsvlm::Input shallow;
        shallow.submergence = 0.5;      // h/c = 0.67, below the linear theory's range
        fsvlm::Result r = fsvlm::Solve( shallow );

        bool warned = r.valid && !r.warning.empty();
        printf( "  %-52s %s\n", "shallow submergence reports a warning", warned ? "ok" : "FAIL" );

        if ( !warned )
        {
            g_failures++;
        }
    }

    printf( "\n%s (%d failure%s)\n", g_failures ? "FAILED" : "PASSED",
            g_failures, g_failures == 1 ? "" : "s" );

    return g_failures ? 1 : 0;
}
