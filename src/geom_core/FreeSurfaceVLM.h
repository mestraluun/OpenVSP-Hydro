//
// This file is released under the terms of the NASA Open Source Agreement (NOSA)
// version 1.3 as detailed in the LICENSE file which accompanies this software.
//

// FreeSurfaceVLM.h: free-surface vortex lattice method for submerged lifting surfaces.
//
//////////////////////////////////////////////////////////////////////
//
// A small, self-contained vortex lattice method for a submerged hydrofoil, including the
// gravity-dependent free-surface (wave making) influence.
//
// The unbounded part is a textbook horseshoe-vortex VLM: one chordwise panel per span
// station, bound vortex at the local quarter chord, collocation point at the local three-
// quarter chord, Biot-Savart influence, flow tangency solve, Kutta-Joukowski forces.
//
// The free-surface part adds a second influence contribution computed from the same bound
// vortex segments, so that
//
//     A_total = A_unbounded + A_free_surface
//
// where the free-surface term is the linearised wave/image system induced by a submerged
// vortex segment beneath z = 0.  It is a function of submergence and of
//
//     kappa0 = g / U^2
//
// which is what makes the result Froude-number dependent -- unlike a plain reflection
// image, this represents a deformable free surface that radiates waves downstream.
//
// Method reference (implemented here from the published formulation, independently of any
// existing implementation):
//   G.D. Thiart, "A generalised vortex lattice method for the analysis of submerged
//   hydrofoils" / free-surface vortex lattice formulation, 1997 & 2001.
//   K.I. Matveev & J.R. Duncan, on hydrofoil system performance prediction tools, which
//   applies the same formulation and provides experimental correlation.
//
// Axis convention: origin on the undisturbed free surface, z positive UP, freestream along
// +x.  A foil submerged by h therefore sits at z = -h.
//
// VALIDITY: linear free-surface theory degrades as the foil approaches the surface.  Below
// a submergence-to-chord ratio of roughly 1.5 the linearisation is unreliable, and below
// about 1.0 it can return a negative near-field wave drag, which is unphysical (Havelock's
// theorem requires wave drag >= 0).  SolveFreeSurfaceVLM reports this through the result's
// warning field rather than silently returning a number.

#if !defined(FREESURFACEVLM__INCLUDED_)
#define FREESURFACEVLM__INCLUDED_

#include <complex>
#include <string>
#include <vector>

namespace fsvlm
{

struct Input
{
    double span         = 4.5;    // Total span of the surface
    double chord        = 0.75;   // Chord (flat plate)
    double submergence  = 1.5;    // Depth of the chord plane below z = 0, positive
    double alpha_deg    = 4.0;    // Angle of attack, degrees
    double U            = 10.29;  // Freestream speed
    double rho          = 1025.0; // Fluid density
    double gravity      = 9.81;   // Gravitational acceleration
    double y0           = 0.0;    // Offset of this surface's centreline from y = 0

    int n_span          = 128;    // Spanwise panels
    int n_nu            = 40;     // Gauss-Legendre points in the free-surface quadrature

    bool free_surface   = true;   // Include the free-surface (wave) influence
    bool cosine_spacing = true;   // Cluster span stations toward the tips
};

struct Result
{
    bool valid = false;
    std::string warning;          // Non-empty when outside the theory's validity range

    double CL = 0.0;
    double CD = 0.0;              // Induced (+ wave, when free_surface) drag coefficient
    double L  = 0.0;              // Dimensional lift
    double D  = 0.0;              // Dimensional drag
    double S  = 0.0;              // Reference area, span * chord
    double q  = 0.0;              // Dynamic pressure

    double froude_chord = 0.0;    // U / sqrt(g * chord)
    double froude_depth = 0.0;    // U / sqrt(g * submergence)
    double h_over_c     = 0.0;

    std::vector<double> y_mid;    // Span station centres
    std::vector<double> gamma;    // Circulation per station
};

// Solve one flow condition.  Returns Result::valid = false with a warning set on bad input.
Result Solve( const Input &in );

// --- Exposed for testing -------------------------------------------------------------

// exp(B) * E1(B) for complex B, evaluated without the overflow/underflow that destroys a
// naive product (exp(B) -> 0 while E1(B) -> inf).
std::complex<double> ExpTimesE1( const std::complex<double> &B );

// Gauss-Legendre nodes and weights on [-1, 1].
void GaussLegendre( int n, std::vector<double> &x, std::vector<double> &w );

} // namespace fsvlm

#endif
