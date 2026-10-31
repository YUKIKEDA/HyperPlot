namespace HyperPlot;

/// <summary>
/// The named colors of CSS Color Module Level 4 (design §4.1, row G7a): the 148 of its table (`named-color-table` in
/// `css-color-4/Overview.bs`), opaque, in its order, with both spellings of the 7 grays. The names are Pascal case as in
/// System.Drawing's named colors (`Cornsilk`, `Honeydew`).
/// </summary>
public static class Colors
{
    /// <summary>Gets <c>aliceblue</c>, #f0f8ff.</summary>
    public static Color AliceBlue { get; } = new(240, 248, 255, 255);

    /// <summary>Gets <c>antiquewhite</c>, #faebd7.</summary>
    public static Color AntiqueWhite { get; } = new(250, 235, 215, 255);

    /// <summary>Gets <c>aqua</c>, #00ffff.</summary>
    public static Color Aqua { get; } = new(0, 255, 255, 255);

    /// <summary>Gets <c>aquamarine</c>, #7fffd4.</summary>
    public static Color Aquamarine { get; } = new(127, 255, 212, 255);

    /// <summary>Gets <c>azure</c>, #f0ffff.</summary>
    public static Color Azure { get; } = new(240, 255, 255, 255);

    /// <summary>Gets <c>beige</c>, #f5f5dc.</summary>
    public static Color Beige { get; } = new(245, 245, 220, 255);

    /// <summary>Gets <c>bisque</c>, #ffe4c4.</summary>
    public static Color Bisque { get; } = new(255, 228, 196, 255);

    /// <summary>Gets <c>black</c>, #000000.</summary>
    public static Color Black { get; } = new(0, 0, 0, 255);

    /// <summary>Gets <c>blanchedalmond</c>, #ffebcd.</summary>
    public static Color BlanchedAlmond { get; } = new(255, 235, 205, 255);

    /// <summary>Gets <c>blue</c>, #0000ff.</summary>
    public static Color Blue { get; } = new(0, 0, 255, 255);

    /// <summary>Gets <c>blueviolet</c>, #8a2be2.</summary>
    public static Color BlueViolet { get; } = new(138, 43, 226, 255);

    /// <summary>Gets <c>brown</c>, #a52a2a.</summary>
    public static Color Brown { get; } = new(165, 42, 42, 255);

    /// <summary>Gets <c>burlywood</c>, #deb887.</summary>
    public static Color BurlyWood { get; } = new(222, 184, 135, 255);

    /// <summary>Gets <c>cadetblue</c>, #5f9ea0.</summary>
    public static Color CadetBlue { get; } = new(95, 158, 160, 255);

    /// <summary>Gets <c>chartreuse</c>, #7fff00.</summary>
    public static Color Chartreuse { get; } = new(127, 255, 0, 255);

    /// <summary>Gets <c>chocolate</c>, #d2691e.</summary>
    public static Color Chocolate { get; } = new(210, 105, 30, 255);

    /// <summary>Gets <c>coral</c>, #ff7f50.</summary>
    public static Color Coral { get; } = new(255, 127, 80, 255);

    /// <summary>Gets <c>cornflowerblue</c>, #6495ed.</summary>
    public static Color CornflowerBlue { get; } = new(100, 149, 237, 255);

    /// <summary>Gets <c>cornsilk</c>, #fff8dc.</summary>
    public static Color Cornsilk { get; } = new(255, 248, 220, 255);

    /// <summary>Gets <c>crimson</c>, #dc143c.</summary>
    public static Color Crimson { get; } = new(220, 20, 60, 255);

    /// <summary>Gets <c>cyan</c>, #00ffff.</summary>
    public static Color Cyan { get; } = new(0, 255, 255, 255);

    /// <summary>Gets <c>darkblue</c>, #00008b.</summary>
    public static Color DarkBlue { get; } = new(0, 0, 139, 255);

    /// <summary>Gets <c>darkcyan</c>, #008b8b.</summary>
    public static Color DarkCyan { get; } = new(0, 139, 139, 255);

    /// <summary>Gets <c>darkgoldenrod</c>, #b8860b.</summary>
    public static Color DarkGoldenrod { get; } = new(184, 134, 11, 255);

    /// <summary>Gets <c>darkgray</c>, #a9a9a9.</summary>
    public static Color DarkGray { get; } = new(169, 169, 169, 255);

    /// <summary>Gets <c>darkgreen</c>, #006400.</summary>
    public static Color DarkGreen { get; } = new(0, 100, 0, 255);

    /// <summary>Gets <c>darkgrey</c>, #a9a9a9.</summary>
    public static Color DarkGrey { get; } = new(169, 169, 169, 255);

    /// <summary>Gets <c>darkkhaki</c>, #bdb76b.</summary>
    public static Color DarkKhaki { get; } = new(189, 183, 107, 255);

    /// <summary>Gets <c>darkmagenta</c>, #8b008b.</summary>
    public static Color DarkMagenta { get; } = new(139, 0, 139, 255);

    /// <summary>Gets <c>darkolivegreen</c>, #556b2f.</summary>
    public static Color DarkOliveGreen { get; } = new(85, 107, 47, 255);

    /// <summary>Gets <c>darkorange</c>, #ff8c00.</summary>
    public static Color DarkOrange { get; } = new(255, 140, 0, 255);

    /// <summary>Gets <c>darkorchid</c>, #9932cc.</summary>
    public static Color DarkOrchid { get; } = new(153, 50, 204, 255);

    /// <summary>Gets <c>darkred</c>, #8b0000.</summary>
    public static Color DarkRed { get; } = new(139, 0, 0, 255);

    /// <summary>Gets <c>darksalmon</c>, #e9967a.</summary>
    public static Color DarkSalmon { get; } = new(233, 150, 122, 255);

    /// <summary>Gets <c>darkseagreen</c>, #8fbc8f.</summary>
    public static Color DarkSeaGreen { get; } = new(143, 188, 143, 255);

    /// <summary>Gets <c>darkslateblue</c>, #483d8b.</summary>
    public static Color DarkSlateBlue { get; } = new(72, 61, 139, 255);

    /// <summary>Gets <c>darkslategray</c>, #2f4f4f.</summary>
    public static Color DarkSlateGray { get; } = new(47, 79, 79, 255);

    /// <summary>Gets <c>darkslategrey</c>, #2f4f4f.</summary>
    public static Color DarkSlateGrey { get; } = new(47, 79, 79, 255);

    /// <summary>Gets <c>darkturquoise</c>, #00ced1.</summary>
    public static Color DarkTurquoise { get; } = new(0, 206, 209, 255);

    /// <summary>Gets <c>darkviolet</c>, #9400d3.</summary>
    public static Color DarkViolet { get; } = new(148, 0, 211, 255);

    /// <summary>Gets <c>deeppink</c>, #ff1493.</summary>
    public static Color DeepPink { get; } = new(255, 20, 147, 255);

    /// <summary>Gets <c>deepskyblue</c>, #00bfff.</summary>
    public static Color DeepSkyBlue { get; } = new(0, 191, 255, 255);

    /// <summary>Gets <c>dimgray</c>, #696969.</summary>
    public static Color DimGray { get; } = new(105, 105, 105, 255);

    /// <summary>Gets <c>dimgrey</c>, #696969.</summary>
    public static Color DimGrey { get; } = new(105, 105, 105, 255);

    /// <summary>Gets <c>dodgerblue</c>, #1e90ff.</summary>
    public static Color DodgerBlue { get; } = new(30, 144, 255, 255);

    /// <summary>Gets <c>firebrick</c>, #b22222.</summary>
    public static Color FireBrick { get; } = new(178, 34, 34, 255);

    /// <summary>Gets <c>floralwhite</c>, #fffaf0.</summary>
    public static Color FloralWhite { get; } = new(255, 250, 240, 255);

    /// <summary>Gets <c>forestgreen</c>, #228b22.</summary>
    public static Color ForestGreen { get; } = new(34, 139, 34, 255);

    /// <summary>Gets <c>fuchsia</c>, #ff00ff.</summary>
    public static Color Fuchsia { get; } = new(255, 0, 255, 255);

    /// <summary>Gets <c>gainsboro</c>, #dcdcdc.</summary>
    public static Color Gainsboro { get; } = new(220, 220, 220, 255);

    /// <summary>Gets <c>ghostwhite</c>, #f8f8ff.</summary>
    public static Color GhostWhite { get; } = new(248, 248, 255, 255);

    /// <summary>Gets <c>gold</c>, #ffd700.</summary>
    public static Color Gold { get; } = new(255, 215, 0, 255);

    /// <summary>Gets <c>goldenrod</c>, #daa520.</summary>
    public static Color Goldenrod { get; } = new(218, 165, 32, 255);

    /// <summary>Gets <c>gray</c>, #808080.</summary>
    public static Color Gray { get; } = new(128, 128, 128, 255);

    /// <summary>Gets <c>green</c>, #008000.</summary>
    public static Color Green { get; } = new(0, 128, 0, 255);

    /// <summary>Gets <c>greenyellow</c>, #adff2f.</summary>
    public static Color GreenYellow { get; } = new(173, 255, 47, 255);

    /// <summary>Gets <c>grey</c>, #808080.</summary>
    public static Color Grey { get; } = new(128, 128, 128, 255);

    /// <summary>Gets <c>honeydew</c>, #f0fff0.</summary>
    public static Color Honeydew { get; } = new(240, 255, 240, 255);

    /// <summary>Gets <c>hotpink</c>, #ff69b4.</summary>
    public static Color HotPink { get; } = new(255, 105, 180, 255);

    /// <summary>Gets <c>indianred</c>, #cd5c5c.</summary>
    public static Color IndianRed { get; } = new(205, 92, 92, 255);

    /// <summary>Gets <c>indigo</c>, #4b0082.</summary>
    public static Color Indigo { get; } = new(75, 0, 130, 255);

    /// <summary>Gets <c>ivory</c>, #fffff0.</summary>
    public static Color Ivory { get; } = new(255, 255, 240, 255);

    /// <summary>Gets <c>khaki</c>, #f0e68c.</summary>
    public static Color Khaki { get; } = new(240, 230, 140, 255);

    /// <summary>Gets <c>lavender</c>, #e6e6fa.</summary>
    public static Color Lavender { get; } = new(230, 230, 250, 255);

    /// <summary>Gets <c>lavenderblush</c>, #fff0f5.</summary>
    public static Color LavenderBlush { get; } = new(255, 240, 245, 255);

    /// <summary>Gets <c>lawngreen</c>, #7cfc00.</summary>
    public static Color LawnGreen { get; } = new(124, 252, 0, 255);

    /// <summary>Gets <c>lemonchiffon</c>, #fffacd.</summary>
    public static Color LemonChiffon { get; } = new(255, 250, 205, 255);

    /// <summary>Gets <c>lightblue</c>, #add8e6.</summary>
    public static Color LightBlue { get; } = new(173, 216, 230, 255);

    /// <summary>Gets <c>lightcoral</c>, #f08080.</summary>
    public static Color LightCoral { get; } = new(240, 128, 128, 255);

    /// <summary>Gets <c>lightcyan</c>, #e0ffff.</summary>
    public static Color LightCyan { get; } = new(224, 255, 255, 255);

    /// <summary>Gets <c>lightgoldenrodyellow</c>, #fafad2.</summary>
    public static Color LightGoldenrodYellow { get; } = new(250, 250, 210, 255);

    /// <summary>Gets <c>lightgray</c>, #d3d3d3.</summary>
    public static Color LightGray { get; } = new(211, 211, 211, 255);

    /// <summary>Gets <c>lightgreen</c>, #90ee90.</summary>
    public static Color LightGreen { get; } = new(144, 238, 144, 255);

    /// <summary>Gets <c>lightgrey</c>, #d3d3d3.</summary>
    public static Color LightGrey { get; } = new(211, 211, 211, 255);

    /// <summary>Gets <c>lightpink</c>, #ffb6c1.</summary>
    public static Color LightPink { get; } = new(255, 182, 193, 255);

    /// <summary>Gets <c>lightsalmon</c>, #ffa07a.</summary>
    public static Color LightSalmon { get; } = new(255, 160, 122, 255);

    /// <summary>Gets <c>lightseagreen</c>, #20b2aa.</summary>
    public static Color LightSeaGreen { get; } = new(32, 178, 170, 255);

    /// <summary>Gets <c>lightskyblue</c>, #87cefa.</summary>
    public static Color LightSkyBlue { get; } = new(135, 206, 250, 255);

    /// <summary>Gets <c>lightslategray</c>, #778899.</summary>
    public static Color LightSlateGray { get; } = new(119, 136, 153, 255);

    /// <summary>Gets <c>lightslategrey</c>, #778899.</summary>
    public static Color LightSlateGrey { get; } = new(119, 136, 153, 255);

    /// <summary>Gets <c>lightsteelblue</c>, #b0c4de.</summary>
    public static Color LightSteelBlue { get; } = new(176, 196, 222, 255);

    /// <summary>Gets <c>lightyellow</c>, #ffffe0.</summary>
    public static Color LightYellow { get; } = new(255, 255, 224, 255);

    /// <summary>Gets <c>lime</c>, #00ff00.</summary>
    public static Color Lime { get; } = new(0, 255, 0, 255);

    /// <summary>Gets <c>limegreen</c>, #32cd32.</summary>
    public static Color LimeGreen { get; } = new(50, 205, 50, 255);

    /// <summary>Gets <c>linen</c>, #faf0e6.</summary>
    public static Color Linen { get; } = new(250, 240, 230, 255);

    /// <summary>Gets <c>magenta</c>, #ff00ff.</summary>
    public static Color Magenta { get; } = new(255, 0, 255, 255);

    /// <summary>Gets <c>maroon</c>, #800000.</summary>
    public static Color Maroon { get; } = new(128, 0, 0, 255);

    /// <summary>Gets <c>mediumaquamarine</c>, #66cdaa.</summary>
    public static Color MediumAquamarine { get; } = new(102, 205, 170, 255);

    /// <summary>Gets <c>mediumblue</c>, #0000cd.</summary>
    public static Color MediumBlue { get; } = new(0, 0, 205, 255);

    /// <summary>Gets <c>mediumorchid</c>, #ba55d3.</summary>
    public static Color MediumOrchid { get; } = new(186, 85, 211, 255);

    /// <summary>Gets <c>mediumpurple</c>, #9370db.</summary>
    public static Color MediumPurple { get; } = new(147, 112, 219, 255);

    /// <summary>Gets <c>mediumseagreen</c>, #3cb371.</summary>
    public static Color MediumSeaGreen { get; } = new(60, 179, 113, 255);

    /// <summary>Gets <c>mediumslateblue</c>, #7b68ee.</summary>
    public static Color MediumSlateBlue { get; } = new(123, 104, 238, 255);

    /// <summary>Gets <c>mediumspringgreen</c>, #00fa9a.</summary>
    public static Color MediumSpringGreen { get; } = new(0, 250, 154, 255);

    /// <summary>Gets <c>mediumturquoise</c>, #48d1cc.</summary>
    public static Color MediumTurquoise { get; } = new(72, 209, 204, 255);

    /// <summary>Gets <c>mediumvioletred</c>, #c71585.</summary>
    public static Color MediumVioletRed { get; } = new(199, 21, 133, 255);

    /// <summary>Gets <c>midnightblue</c>, #191970.</summary>
    public static Color MidnightBlue { get; } = new(25, 25, 112, 255);

    /// <summary>Gets <c>mintcream</c>, #f5fffa.</summary>
    public static Color MintCream { get; } = new(245, 255, 250, 255);

    /// <summary>Gets <c>mistyrose</c>, #ffe4e1.</summary>
    public static Color MistyRose { get; } = new(255, 228, 225, 255);

    /// <summary>Gets <c>moccasin</c>, #ffe4b5.</summary>
    public static Color Moccasin { get; } = new(255, 228, 181, 255);

    /// <summary>Gets <c>navajowhite</c>, #ffdead.</summary>
    public static Color NavajoWhite { get; } = new(255, 222, 173, 255);

    /// <summary>Gets <c>navy</c>, #000080.</summary>
    public static Color Navy { get; } = new(0, 0, 128, 255);

    /// <summary>Gets <c>oldlace</c>, #fdf5e6.</summary>
    public static Color OldLace { get; } = new(253, 245, 230, 255);

    /// <summary>Gets <c>olive</c>, #808000.</summary>
    public static Color Olive { get; } = new(128, 128, 0, 255);

    /// <summary>Gets <c>olivedrab</c>, #6b8e23.</summary>
    public static Color OliveDrab { get; } = new(107, 142, 35, 255);

    /// <summary>Gets <c>orange</c>, #ffa500.</summary>
    public static Color Orange { get; } = new(255, 165, 0, 255);

    /// <summary>Gets <c>orangered</c>, #ff4500.</summary>
    public static Color OrangeRed { get; } = new(255, 69, 0, 255);

    /// <summary>Gets <c>orchid</c>, #da70d6.</summary>
    public static Color Orchid { get; } = new(218, 112, 214, 255);

    /// <summary>Gets <c>palegoldenrod</c>, #eee8aa.</summary>
    public static Color PaleGoldenrod { get; } = new(238, 232, 170, 255);

    /// <summary>Gets <c>palegreen</c>, #98fb98.</summary>
    public static Color PaleGreen { get; } = new(152, 251, 152, 255);

    /// <summary>Gets <c>paleturquoise</c>, #afeeee.</summary>
    public static Color PaleTurquoise { get; } = new(175, 238, 238, 255);

    /// <summary>Gets <c>palevioletred</c>, #db7093.</summary>
    public static Color PaleVioletRed { get; } = new(219, 112, 147, 255);

    /// <summary>Gets <c>papayawhip</c>, #ffefd5.</summary>
    public static Color PapayaWhip { get; } = new(255, 239, 213, 255);

    /// <summary>Gets <c>peachpuff</c>, #ffdab9.</summary>
    public static Color PeachPuff { get; } = new(255, 218, 185, 255);

    /// <summary>Gets <c>peru</c>, #cd853f.</summary>
    public static Color Peru { get; } = new(205, 133, 63, 255);

    /// <summary>Gets <c>pink</c>, #ffc0cb.</summary>
    public static Color Pink { get; } = new(255, 192, 203, 255);

    /// <summary>Gets <c>plum</c>, #dda0dd.</summary>
    public static Color Plum { get; } = new(221, 160, 221, 255);

    /// <summary>Gets <c>powderblue</c>, #b0e0e6.</summary>
    public static Color PowderBlue { get; } = new(176, 224, 230, 255);

    /// <summary>Gets <c>purple</c>, #800080.</summary>
    public static Color Purple { get; } = new(128, 0, 128, 255);

    /// <summary>Gets <c>rebeccapurple</c>, #663399.</summary>
    public static Color RebeccaPurple { get; } = new(102, 51, 153, 255);

    /// <summary>Gets <c>red</c>, #ff0000.</summary>
    public static Color Red { get; } = new(255, 0, 0, 255);

    /// <summary>Gets <c>rosybrown</c>, #bc8f8f.</summary>
    public static Color RosyBrown { get; } = new(188, 143, 143, 255);

    /// <summary>Gets <c>royalblue</c>, #4169e1.</summary>
    public static Color RoyalBlue { get; } = new(65, 105, 225, 255);

    /// <summary>Gets <c>saddlebrown</c>, #8b4513.</summary>
    public static Color SaddleBrown { get; } = new(139, 69, 19, 255);

    /// <summary>Gets <c>salmon</c>, #fa8072.</summary>
    public static Color Salmon { get; } = new(250, 128, 114, 255);

    /// <summary>Gets <c>sandybrown</c>, #f4a460.</summary>
    public static Color SandyBrown { get; } = new(244, 164, 96, 255);

    /// <summary>Gets <c>seagreen</c>, #2e8b57.</summary>
    public static Color SeaGreen { get; } = new(46, 139, 87, 255);

    /// <summary>Gets <c>seashell</c>, #fff5ee.</summary>
    public static Color SeaShell { get; } = new(255, 245, 238, 255);

    /// <summary>Gets <c>sienna</c>, #a0522d.</summary>
    public static Color Sienna { get; } = new(160, 82, 45, 255);

    /// <summary>Gets <c>silver</c>, #c0c0c0.</summary>
    public static Color Silver { get; } = new(192, 192, 192, 255);

    /// <summary>Gets <c>skyblue</c>, #87ceeb.</summary>
    public static Color SkyBlue { get; } = new(135, 206, 235, 255);

    /// <summary>Gets <c>slateblue</c>, #6a5acd.</summary>
    public static Color SlateBlue { get; } = new(106, 90, 205, 255);

    /// <summary>Gets <c>slategray</c>, #708090.</summary>
    public static Color SlateGray { get; } = new(112, 128, 144, 255);

    /// <summary>Gets <c>slategrey</c>, #708090.</summary>
    public static Color SlateGrey { get; } = new(112, 128, 144, 255);

    /// <summary>Gets <c>snow</c>, #fffafa.</summary>
    public static Color Snow { get; } = new(255, 250, 250, 255);

    /// <summary>Gets <c>springgreen</c>, #00ff7f.</summary>
    public static Color SpringGreen { get; } = new(0, 255, 127, 255);

    /// <summary>Gets <c>steelblue</c>, #4682b4.</summary>
    public static Color SteelBlue { get; } = new(70, 130, 180, 255);

    /// <summary>Gets <c>tan</c>, #d2b48c.</summary>
    public static Color Tan { get; } = new(210, 180, 140, 255);

    /// <summary>Gets <c>teal</c>, #008080.</summary>
    public static Color Teal { get; } = new(0, 128, 128, 255);

    /// <summary>Gets <c>thistle</c>, #d8bfd8.</summary>
    public static Color Thistle { get; } = new(216, 191, 216, 255);

    /// <summary>Gets <c>tomato</c>, #ff6347.</summary>
    public static Color Tomato { get; } = new(255, 99, 71, 255);

    /// <summary>Gets <c>turquoise</c>, #40e0d0.</summary>
    public static Color Turquoise { get; } = new(64, 224, 208, 255);

    /// <summary>Gets <c>violet</c>, #ee82ee.</summary>
    public static Color Violet { get; } = new(238, 130, 238, 255);

    /// <summary>Gets <c>wheat</c>, #f5deb3.</summary>
    public static Color Wheat { get; } = new(245, 222, 179, 255);

    /// <summary>Gets <c>white</c>, #ffffff.</summary>
    public static Color White { get; } = new(255, 255, 255, 255);

    /// <summary>Gets <c>whitesmoke</c>, #f5f5f5.</summary>
    public static Color WhiteSmoke { get; } = new(245, 245, 245, 255);

    /// <summary>Gets <c>yellow</c>, #ffff00.</summary>
    public static Color Yellow { get; } = new(255, 255, 0, 255);

    /// <summary>Gets <c>yellowgreen</c>, #9acd32.</summary>
    public static Color YellowGreen { get; } = new(154, 205, 50, 255);
}
