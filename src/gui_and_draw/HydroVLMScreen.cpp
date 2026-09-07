//
// This file is released under the terms of the NASA Open Source Agreement (NOSA)
// version 1.3 as detailed in the LICENSE file which accompanies this software.
//

#include "HydroVLMScreen.h"
#include "AnalysisMgr.h"
#include "ScreenMgr.h"

#include <stdexcept>

HydroVLMScreen::HydroVLMScreen( ScreenMgr* mgr ) :
    BasicScreen( mgr, 430, 430, "HydroVLM — finite-Froude free surface" )
{
    m_FLTK_Window->callback( staticCloseCB, this );
    m_MainLayout.SetGroupAndScreen( m_FLTK_Window, this );
    m_MainLayout.AddX( 8 );
    m_MainLayout.AddY( 8 );
    m_MainLayout.AddSubGroupLayout( m_BorderLayout,
                                    m_MainLayout.GetRemainX() - 8,
                                    m_MainLayout.GetRemainY() - 8 );

    m_BorderLayout.AddDividerBox( "Hydrofoil and water inputs" );
    m_BorderLayout.SetButtonWidth( 155 );
    m_BorderLayout.AddInput( m_SpanInput, "Span" );
    m_BorderLayout.AddInput( m_ChordInput, "Chord" );
    m_BorderLayout.AddInput( m_SubmergenceInput, "Submergence" );
    m_BorderLayout.AddInput( m_AlphaInput, "Angle of attack (deg)" );
    m_BorderLayout.AddInput( m_VinfInput, "Speed" );
    m_BorderLayout.AddInput( m_RhoInput, "Water density" );
    m_BorderLayout.AddInput( m_GravityInput, "Gravity" );
    m_BorderLayout.AddInput( m_SpanPanelsInput, "Span panels" );
    m_BorderLayout.AddInput( m_QuadPointsInput, "Wave quadrature points" );
    m_BorderLayout.AddYGap();
    m_BorderLayout.AddDividerBox( "Finite-Froude linear free surface at z = 0" );
    m_BorderLayout.AddOutput( m_StatusOutput, "Status" );
    m_BorderLayout.AddYGap();
    m_BorderLayout.AddButton( m_RunButton, "Run HydroVLM" );

    m_SpanInput.Update( "4.5" );
    m_ChordInput.Update( "0.75" );
    m_SubmergenceInput.Update( "1.5" );
    m_AlphaInput.Update( "4.0" );
    m_VinfInput.Update( "10.288" );
    m_RhoInput.Update( "1025.0" );
    m_GravityInput.Update( "9.80665" );
    m_SpanPanelsInput.Update( "16" );
    m_QuadPointsInput.Update( "40" );
    m_LastStatus = "Ready";
}

bool HydroVLMScreen::Update()
{
    BasicScreen::Update();
    m_StatusOutput.Update( m_LastStatus );
    return true;
}

void HydroVLMScreen::GuiDeviceCallBack( GuiDevice* device )
{
    if ( device == &m_RunButton )
    {
        try
        {
            const string analysis = "FreeSurfaceVLM";
            AnalysisMgr.SetAnalysisInputDefaults( analysis );
            AnalysisMgr.SetDoubleAnalysisInput( analysis, "Span", { std::stod( m_SpanInput.GetString() ) } );
            AnalysisMgr.SetDoubleAnalysisInput( analysis, "Chord", { std::stod( m_ChordInput.GetString() ) } );
            AnalysisMgr.SetDoubleAnalysisInput( analysis, "Submergence", { std::stod( m_SubmergenceInput.GetString() ) } );
            AnalysisMgr.SetDoubleAnalysisInput( analysis, "Alpha", { std::stod( m_AlphaInput.GetString() ) } );
            AnalysisMgr.SetDoubleAnalysisInput( analysis, "Vinf", { std::stod( m_VinfInput.GetString() ) } );
            AnalysisMgr.SetDoubleAnalysisInput( analysis, "Rho", { std::stod( m_RhoInput.GetString() ) } );
            AnalysisMgr.SetDoubleAnalysisInput( analysis, "Gravity", { std::stod( m_GravityInput.GetString() ) } );
            AnalysisMgr.SetIntAnalysisInput( analysis, "NumSpanPanels", { std::stoi( m_SpanPanelsInput.GetString() ) } );
            AnalysisMgr.SetIntAnalysisInput( analysis, "NumQuadPoints", { std::stoi( m_QuadPointsInput.GetString() ) } );
            AnalysisMgr.SetIntAnalysisInput( analysis, "FreeSurfaceFlag", { 1 } );
            AnalysisMgr.SetIntAnalysisInput( analysis, "CosineSpacing", { 1 } );

            const string result_id = AnalysisMgr.ExecAnalysis( analysis );
            if ( result_id.empty() )
            {
                throw std::runtime_error( "HydroVLM returned no result." );
            }
            m_LastStatus = "Solved — result " + result_id;
        }
        catch ( const std::exception& e )
        {
            m_LastStatus = "Failed";
            m_ScreenMgr->Alert( string( "HydroVLM: " ) + e.what() );
        }
        Update();
    }
}

void HydroVLMScreen::CallBack( Fl_Widget* w )
{
    BasicScreen::CallBack( w );
}

void HydroVLMScreen::CloseCallBack( Fl_Widget* w )
{
    Hide();
}
