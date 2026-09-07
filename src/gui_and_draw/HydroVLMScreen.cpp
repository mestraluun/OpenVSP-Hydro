//
// This file is released under the terms of the NASA Open Source Agreement (NOSA)
// version 1.3 as detailed in the LICENSE file which accompanies this software.
//

#include "HydroVLMScreen.h"
#include "ScreenMgr.h"

HydroVLMScreen::HydroVLMScreen( ScreenMgr* mgr ) :
    BasicScreen( mgr, 430, 250, "HydroVLM" )
{
    m_FLTK_Window->callback( staticCloseCB, this );
    m_MainLayout.SetGroupAndScreen( m_FLTK_Window, this );
    m_MainLayout.AddX( 8 );
    m_MainLayout.AddY( 8 );
    m_MainLayout.AddSubGroupLayout( m_BorderLayout,
                                    m_MainLayout.GetRemainX() - 8,
                                    m_MainLayout.GetRemainY() - 8 );

    m_BorderLayout.AddDividerBox( "Hydrofoil analysis" );
    m_BorderLayout.SetButtonWidth( 155 );
    m_BorderLayout.AddOutput( m_ModelOutput, "Solver" );
    m_BorderLayout.AddOutput( m_SurfaceOutput, "Free surface" );
    m_BorderLayout.AddOutput( m_StatusOutput, "Status" );
    m_BorderLayout.AddYGap();
    m_BorderLayout.AddButton( m_RunButton, "Run HydroVLM" );
}

bool HydroVLMScreen::Update()
{
    BasicScreen::Update();
    m_ModelOutput.Update( "External HydroVLM C++ core" );
    m_SurfaceOutput.Update( "Finite-Froude linear (z = 0)" );
    m_StatusOutput.Update( "Geometry adapter not connected" );
    return true;
}

void HydroVLMScreen::GuiDeviceCallBack( GuiDevice* device )
{
    if ( device == &m_RunButton )
    {
        m_ScreenMgr->Alert(
            "HydroVLM is registered separately from VSPAERO.\n"
            "The next integration step connects OpenVSP wing geometry to the "
            "external finite-Froude HydroVLM solver." );
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
