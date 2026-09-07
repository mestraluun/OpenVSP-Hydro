//
// This file is released under the terms of the NASA Open Source Agreement (NOSA)
// version 1.3 as detailed in the LICENSE file which accompanies this software.
//

// FreeSurfaceVLM.cpp: free-surface vortex lattice method for submerged lifting surfaces.
//
//////////////////////////////////////////////////////////////////////

#include "FreeSurfaceVLM.h"

#include <algorithm>
#include <cmath>

namespace fsvlm
{

namespace
{

const double PI    = 3.14159265358979323846;
const double EULER = 0.57721566490153286061;   // Euler-Mascheroni constant

typedef std::complex<double> cdouble;

//==== Minimal 3-vector ====//

struct Vec3
{
    double x, y, z;

    Vec3() : x( 0 ), y( 0 ), z( 0 ) {}
    Vec3( double xx, double yy, double zz ) : x( xx ), y( yy ), z( zz ) {}
};

inline Vec3 operator+( const Vec3 &a, const Vec3 &b ) { return Vec3( a.x + b.x, a.y + b.y, a.z + b.z ); }
inline Vec3 operator-( const Vec3 &a, const Vec3 &b ) { return Vec3( a.x - b.x, a.y - b.y, a.z - b.z ); }
inline Vec3 operator*( const Vec3 &a, double s )      { return Vec3( a.x * s, a.y * s, a.z * s ); }
inline double Dot( const Vec3 &a, const Vec3 &b )     { return a.x * b.x + a.y * b.y + a.z * b.z; }
inline Vec3 Cross( const Vec3 &a, const Vec3 &b )
{
    return Vec3( a.y * b.z - a.z * b.y,
                 a.z * b.x - a.x * b.z,
                 a.x * b.y - a.y * b.x );
}
inline double Mag( const Vec3 &a ) { return std::sqrt( Dot( a, a ) ); }

//==== E1 by its power series, valid on the principal branch ====//
//
// E1(z) = -gamma - ln(z) + sum_{k>=1} (-1)^(k+1) z^k / (k k!)
//
// This solver only ever evaluates E1 with Re(z) <= 0 (both the field point and the source
// point are submerged, so z + zeta <= 0).  There the terms do not alternate in sign, so the
// sum is well conditioned and no cancellation occurs, which is what makes the plain series
// usable out to the |B| = 25 handover point.

cdouble E1Series( const cdouble &z )
{
    cdouble sum( 0.0, 0.0 );
    cdouble term( 1.0, 0.0 );

    for ( int k = 1; k < 400; k++ )
    {
        term *= z / static_cast < double > ( k );          // z^k / k!
        cdouble contrib = term / static_cast < double > ( k );

        if ( ( k % 2 ) == 1 )
        {
            sum += contrib;
        }
        else
        {
            sum -= contrib;
        }

        if ( std::abs( contrib ) <= 1.0e-18 * std::abs( sum ) && k > 2 )
        {
            break;
        }
    }

    return -EULER - std::log( z ) + sum;
}

} // anonymous namespace

//==== exp(B) * E1(B), stable for all |B| ====//
//
// Evaluating the product term by term overflows: for large |B| with Re(B) < 0, exp(B)
// underflows to zero while E1(B) overflows, giving 0 * inf.  Past |B| = 25 the asymptotic
// expansion of the product is used instead, which is plain arithmetic and is accurate
// exactly where the direct form fails.

cdouble ExpTimesE1( const cdouble &B )
{
    double magB = std::abs( B );

    if ( magB > 25.0 )
    {
        // exp(B) E1(B) ~ (1/B) sum_k (-1)^k k! / B^k
        cdouble inv = 1.0 / B;
        cdouble term( 1.0, 0.0 );
        cdouble sum( 1.0, 0.0 );

        for ( int k = 1; k <= 12; k++ )
        {
            term *= -static_cast < double > ( k ) * inv;
            sum += term;
        }

        return inv * sum;
    }

    return std::exp( B ) * E1Series( B );
}

//==== Gauss-Legendre nodes and weights on [-1,1] ====//

void GaussLegendre( int n, std::vector < double > &x, std::vector < double > &w )
{
    x.assign( std::max( n, 0 ), 0.0 );
    w.assign( std::max( n, 0 ), 0.0 );

    if ( n < 1 )
    {
        return;
    }

    int m = ( n + 1 ) / 2;

    for ( int i = 0; i < m; i++ )
    {
        // Chebyshev starting guess, then Newton on P_n.
        double z = std::cos( PI * ( i + 0.75 ) / ( n + 0.5 ) );
        double pp = 0.0;

        for ( int it = 0; it < 100; it++ )
        {
            double p0 = 1.0, p1 = 0.0;

            for ( int j = 0; j < n; j++ )
            {
                double p2 = p1;
                p1 = p0;
                p0 = ( ( 2.0 * j + 1.0 ) * z * p1 - j * p2 ) / ( j + 1.0 );
            }

            pp = n * ( z * p0 - p1 ) / ( z * z - 1.0 );

            double dz = p0 / pp;
            z -= dz;

            if ( std::abs( dz ) < 1.0e-15 )
            {
                break;
            }
        }

        x[i]         = -z;
        x[n - 1 - i] =  z;
        w[i]         = 2.0 / ( ( 1.0 - z * z ) * pp * pp );
        w[n - 1 - i] = w[i];
    }
}

namespace
{

//==== Biot-Savart for a unit strength straight filament A -> B ====//
//
// The squared cross product is floored, so a point lying on the filament yields an exactly
// zero cross product and therefore zero induced velocity.  That is what removes a bound
// segment's singular self-induction when velocities are evaluated at bound midpoints for
// the Kutta-Joukowski force integration.

Vec3 SegmentVelocity( const Vec3 &P, const Vec3 &A, const Vec3 &B )
{
    Vec3 r1 = P - A;
    Vec3 r2 = P - B;
    Vec3 r0 = B - A;

    Vec3 cr = Cross( r1, r2 );
    double cr2 = Dot( cr, cr );

    if ( cr2 < 1.0e-9 )
    {
        cr2 = 1.0e-9;
    }

    double r1n = Mag( r1 );
    double r2n = Mag( r2 );

    if ( r1n < 1.0e-12 || r2n < 1.0e-12 )
    {
        return Vec3();
    }

    double K = ( 1.0 / ( 4.0 * PI ) ) * ( Dot( r0, r1 ) / r1n - Dot( r0, r2 ) / r2n ) / cr2;

    return cr * K;
}

//==== Unbounded horseshoe influence ====//

Vec3 HorseshoeVelocity( const Vec3 &P, const Vec3 &p1, const Vec3 &p2, double x_far )
{
    Vec3 far1( x_far, p1.y, p1.z );
    Vec3 far2( x_far, p2.y, p2.z );

    return SegmentVelocity( P, p1, p2 )
         + SegmentVelocity( P, far1, p1 )
         + SegmentVelocity( P, p2, far2 );
}

//==== Free-surface kernel ====//
//
// G = 1/w - kappa * exp(B) E1(B), with w = (z + zeta) + i*omega and B = kappa * w.
//
// Written as one quantity on purpose.  For large kappa the two terms individually approach
// each other and differencing them directly loses all significant digits -- which is the
// regime the quadrature spends most of its nodes in, since kappa = kappa0 / cos^2(nu) grows
// without bound toward nu = +-pi/2.  Substituting the asymptotic series and cancelling the
// leading term analytically gives
//
//     G = (1/w) * sum_{k>=1} (-1)^(k+1) k! / B^k
//
// which is accurate and, just as importantly, decays like 1/(kappa w^2) so the integrand
// dies off toward the endpoints as it should.

cdouble KernelG( const cdouble &w, double kappa )
{
    cdouble B = w * kappa;

    if ( std::abs( B ) > 25.0 )
    {
        cdouble inv = 1.0 / B;
        cdouble term( 1.0, 0.0 );
        cdouble sum( 0.0, 0.0 );

        for ( int k = 1; k <= 12; k++ )
        {
            term *= -static_cast < double > ( k ) * inv;   // (-1)^k k! / B^k
            sum -= term;                                   // accumulate (-1)^(k+1) k! / B^k
        }

        return sum / w;
    }

    return 1.0 / w - kappa * ExpTimesE1( B );
}

// J at a field point due to one endpoint of a bound segment, for one quadrature angle.
cdouble KernelJ( double nu, double x, double y, double z,
                 double xi, double eta, double zeta, double kappa0 )
{
    double cn = std::cos( nu );
    double kappa = kappa0 / ( cn * cn );

    double omega = ( x - xi ) * cn + ( y - eta ) * std::sin( nu );
    double zsum  = z + zeta;

    cdouble w( zsum, omega );

    // Downstream wave term.  zsum < 0 for a submerged foil so the exponential decays; it
    // underflows harmlessly to zero for the very large kappa near the quadrature endpoints.
    double heaviside = ( omega > 0.0 ) ? 1.0 : ( ( omega == 0.0 ) ? 0.5 : 0.0 );

    cdouble wave( 0.0, 0.0 );

    if ( heaviside > 0.0 )
    {
        double decay = kappa * zsum;

        if ( decay > -700.0 )     // else exp underflows to zero anyway
        {
            wave = cdouble( 0.0, -2.0 ) * heaviside * kappa * std::exp( kappa * w );
        }
    }

    return wave + KernelG( w, kappa ) / PI;
}

//==== Dense solve, Gaussian elimination with partial pivoting ====//

bool LinearSolve( std::vector < double > &A, std::vector < double > &b, int n )
{
    for ( int col = 0; col < n; col++ )
    {
        int piv = col;
        double best = std::abs( A[col * n + col] );

        for ( int r = col + 1; r < n; r++ )
        {
            double v = std::abs( A[r * n + col] );
            if ( v > best )
            {
                best = v;
                piv = r;
            }
        }

        if ( best < 1.0e-14 )
        {
            return false;
        }

        if ( piv != col )
        {
            for ( int c = 0; c < n; c++ )
            {
                std::swap( A[col * n + c], A[piv * n + c] );
            }
            std::swap( b[col], b[piv] );
        }

        double d = A[col * n + col];

        for ( int r = col + 1; r < n; r++ )
        {
            double f = A[r * n + col] / d;

            if ( f == 0.0 )
            {
                continue;
            }

            for ( int c = col; c < n; c++ )
            {
                A[r * n + c] -= f * A[col * n + c];
            }
            b[r] -= f * b[col];
        }
    }

    for ( int r = n - 1; r >= 0; r-- )
    {
        double s = b[r];

        for ( int c = r + 1; c < n; c++ )
        {
            s -= A[r * n + c] * b[c];
        }

        b[r] = s / A[r * n + r];
    }

    return true;
}

} // anonymous namespace

//==== Solve one flow condition ====//

Result Solve( const Input &in )
{
    Result res;

    if ( in.n_span < 1 || in.span <= 0.0 || in.chord <= 0.0 || in.U <= 0.0 || in.rho <= 0.0 )
    {
        res.warning = "Invalid input: span, chord, U, rho and n_span must all be positive.";
        return res;
    }

    if ( in.free_surface && in.submergence <= 0.0 )
    {
        res.warning = "Invalid input: submergence must be positive for a free-surface solve.";
        return res;
    }

    const int N = in.n_span;

    // ---- Lattice ----------------------------------------------------------------------

    std::vector < double > y_edge( N + 1 );

    for ( int i = 0; i <= N; i++ )
    {
        if ( in.cosine_spacing )
        {
            // Cluster toward both tips; uniform spacing under-resolves the tip vortex and
            // systematically under-predicts induced drag.
            double theta = PI * i / static_cast < double > ( N );
            y_edge[i] = -0.5 * in.span * std::cos( theta ) + in.y0;
        }
        else
        {
            y_edge[i] = -0.5 * in.span + in.span * i / static_cast < double > ( N ) + in.y0;
        }
    }

    double zplane = -in.submergence;

    std::vector < Vec3 > bp1( N ), bp2( N ), colloc( N ), mid( N );
    res.y_mid.resize( N );

    for ( int i = 0; i < N; i++ )
    {
        double ym = 0.5 * ( y_edge[i] + y_edge[i + 1] );

        bp1[i]    = Vec3( 0.25 * in.chord, y_edge[i],     zplane );
        bp2[i]    = Vec3( 0.25 * in.chord, y_edge[i + 1], zplane );
        colloc[i] = Vec3( 0.75 * in.chord, ym,            zplane );
        mid[i]    = Vec3( 0.25 * in.chord, ym,            zplane );

        res.y_mid[i] = ym;
    }

    const Vec3 normal( 0.0, 0.0, 1.0 );
    const double x_far = 1.0e6;
    double kappa0 = in.gravity / ( in.U * in.U );

    std::vector < double > nu, wq;

    if ( in.free_surface )
    {
        GaussLegendre( in.n_nu, nu, wq );

        for ( int k = 0; k < in.n_nu; k++ )
        {
            nu[k] *= 0.5 * PI;      // map [-1,1] -> [-pi/2, pi/2]
            wq[k] *= 0.5 * PI;
        }
    }

    // Accumulates the free-surface influence of every panel at a set of field points.
    struct WaveAccumulator
    {
        static void Add( std::vector < Vec3 > &aic, const std::vector < Vec3 > &pts,
                         const std::vector < Vec3 > &p1, const std::vector < Vec3 > &p2,
                         const std::vector < double > &nu, const std::vector < double > &wq,
                         double kappa0, int N )
        {
            for ( size_t k = 0; k < nu.size(); k++ )
            {
                double cn = std::cos( nu[k] );
                double sn = std::sin( nu[k] );
                double tn = sn / cn;

                for ( int j = 0; j < N; j++ )
                {
                    double dEta  = p2[j].y - p1[j].y;
                    double dZeta = p2[j].z - p1[j].z;
                    double dXi   = p2[j].x - p1[j].x;

                    cdouble num( dEta / cn, dZeta * tn );
                    cdouble den( dXi * cn + dEta * sn, dZeta );

                    if ( std::abs( den ) < 1.0e-300 )
                    {
                        continue;
                    }

                    cdouble pre = num / den;

                    for ( int i = 0; i < N; i++ )
                    {
                        cdouble J2 = KernelJ( nu[k], pts[i].x, pts[i].y, pts[i].z,
                                              p2[j].x, p2[j].y, p2[j].z, kappa0 );
                        cdouble J1 = KernelJ( nu[k], pts[i].x, pts[i].y, pts[i].z,
                                              p1[j].x, p1[j].y, p1[j].z, kappa0 );

                        cdouble f = pre * ( J2 - J1 );

                        // Scaled by 1/(2 pi) once the angle integral is complete.
                        aic[i * N + j].x += f.imag() * cn * wq[k];
                        aic[i * N + j].y += f.imag() * sn * wq[k];
                        aic[i * N + j].z += f.real()      * wq[k];
                    }
                }
            }

            double s = 1.0 / ( 2.0 * PI );

            for ( size_t n = 0; n < aic.size(); n++ )
            {
                aic[n] = aic[n] * s;
            }
        }
    };

    // ---- Influence at the collocation points ------------------------------------------

    std::vector < Vec3 > aic( static_cast < size_t > ( N ) * N );

    for ( int i = 0; i < N; i++ )
    {
        for ( int j = 0; j < N; j++ )
        {
            aic[static_cast < size_t > ( i ) * N + j] = HorseshoeVelocity( colloc[i], bp1[j], bp2[j], x_far );
        }
    }

    if ( in.free_surface )
    {
        std::vector < Vec3 > wave( static_cast < size_t > ( N ) * N );
        WaveAccumulator::Add( wave, colloc, bp1, bp2, nu, wq, kappa0, N );

        for ( size_t n = 0; n < aic.size(); n++ )
        {
            aic[n] = aic[n] + wave[n];
        }
    }

    // ---- Flow tangency ----------------------------------------------------------------

    double alpha = in.alpha_deg * PI / 180.0;
    Vec3 Uinf( in.U * std::cos( alpha ), 0.0, in.U * std::sin( alpha ) );

    std::vector < double > A( static_cast < size_t > ( N ) * N );
    std::vector < double > rhs( N );

    for ( int i = 0; i < N; i++ )
    {
        for ( int j = 0; j < N; j++ )
        {
            A[static_cast < size_t > ( i ) * N + j] = Dot( aic[static_cast < size_t > ( i ) * N + j], normal );
        }

        rhs[i] = -Dot( Uinf, normal );
    }

    if ( !LinearSolve( A, rhs, N ) )
    {
        res.warning = "Singular influence matrix; check the lattice definition.";
        return res;
    }

    std::vector < double > gamma = rhs;
    res.gamma = gamma;

    // ---- Kutta-Joukowski forces at the bound midpoints --------------------------------

    std::vector < Vec3 > vind( static_cast < size_t > ( N ) * N );

    for ( int i = 0; i < N; i++ )
    {
        for ( int j = 0; j < N; j++ )
        {
            vind[static_cast < size_t > ( i ) * N + j] = HorseshoeVelocity( mid[i], bp1[j], bp2[j], x_far );
        }
    }

    if ( in.free_surface )
    {
        std::vector < Vec3 > wave( static_cast < size_t > ( N ) * N );
        WaveAccumulator::Add( wave, mid, bp1, bp2, nu, wq, kappa0, N );

        for ( size_t n = 0; n < vind.size(); n++ )
        {
            vind[n] = vind[n] + wave[n];
        }
    }

    Vec3 Ftot;

    for ( int i = 0; i < N; i++ )
    {
        Vec3 v = Uinf;

        for ( int j = 0; j < N; j++ )
        {
            v = v + vind[static_cast < size_t > ( i ) * N + j] * gamma[j];
        }

        Vec3 dl = bp2[i] - bp1[i];
        Vec3 F  = Cross( v, dl ) * ( in.rho * gamma[i] );

        Ftot = Ftot + F;
    }

    res.S = in.span * in.chord;
    res.q = 0.5 * in.rho * in.U * in.U;

    res.L = Ftot.z * std::cos( alpha ) - Ftot.x * std::sin( alpha );
    res.D = Ftot.x * std::cos( alpha ) + Ftot.z * std::sin( alpha );

    res.CL = res.L / ( res.q * res.S );
    res.CD = res.D / ( res.q * res.S );

    res.froude_chord = in.U / std::sqrt( in.gravity * in.chord );
    res.h_over_c     = in.submergence / in.chord;

    if ( in.submergence > 0.0 )
    {
        res.froude_depth = in.U / std::sqrt( in.gravity * in.submergence );
    }

    if ( in.free_surface && res.h_over_c < 1.5 )
    {
        res.warning = "Submergence is below 1.5 chords; linear free-surface theory is "
                      "unreliable here and the wave drag may come out negative, which is "
                      "unphysical.  Treat the result as qualitative.";
    }

    res.valid = true;

    return res;
}

} // namespace fsvlm
