﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Drawing.Drawing2D.Tests;

public class BlendTests
{
    [Fact]
    public void Ctor_Default()
    {
        Blend blend = new();
        Assert.Equal(new float[1], blend.Factors);
        Assert.Equal(new float[1], blend.Positions);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Ctor_Count(int count)
    {
        Blend blend = new(count);
        Assert.Equal(new float[count], blend.Factors);
        Assert.Equal(new float[count], blend.Positions);
    }

    [Fact]
    public void Ctor_InvalidCount_ThrowsOverflowException()
    {
        Assert.Throws<OverflowException>(() => new Blend(-1));
    }

    [Fact(Skip = "Condition not met", SkipType = typeof(PlatformDetection), SkipUnless = nameof(PlatformDetection.IsNotIntMaxValueArrayIndexSupported))]
    public void Ctor_LargeCount_ThrowsOutOfMemoryException()
    {
        Assert.Throws<OutOfMemoryException>(() => new Blend(int.MaxValue));
    }

    [Fact]
    public void Factors_Set_GetReturnsExpected()
    {
        Blend blend = new() { Factors = null };
        Assert.Null(blend.Factors);

        blend.Factors = new float[10];
        Assert.Equal(new float[10], blend.Factors);
    }

    [Fact]
    public void Positions_Set_GetReturnsExpected()
    {
        Blend blend = new() { Positions = null };
        Assert.Null(blend.Positions);

        blend.Positions = new float[10];
        Assert.Equal(new float[10], blend.Positions);
    }
}
